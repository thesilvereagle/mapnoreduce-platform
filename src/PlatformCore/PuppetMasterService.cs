﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PlatformCore.Exception;
using SharedTypes;

namespace PlatformCore
{
	[Serializable]
	public class PuppetMasterService : MarshalByRefObject, IPuppetMasterService
	{
		private const int SYSTEMPARAM_SHARE = 3;

		private readonly object globalLock = new object();
		private readonly object workersLock = new object();
		private readonly object workerShareLock = new object();
		private readonly object jobTrackersQueueLock = new object();
		private readonly Dictionary<int, IWorker> workersAvailable = new Dictionary<int, IWorker>();
		private readonly Dictionary<int, IWorker> workersInUse = new Dictionary<int, IWorker>();
		private readonly List<Uri> jobTrackersMaster = new List<Uri>();
		private volatile List<Uri> knownPms = new List<Uri>();
		private readonly Queue<Tuple<int, Uri>> jobTrackersWaitingQueue = new Queue<Tuple<int, Uri>>();

		public static readonly string ServiceName = "PM";
		public static readonly Uri ServiceUrl = Globals.LocalPuppetMasterUri;

		public PuppetMasterService() {
		}

		public Uri GetServiceUri() {
			return ServiceUrl;
		}

		public List<Uri> KnownPmsUris {
			get {
				return knownPms;
			}
		}

		public Dictionary<int, IWorker> WorkersRegistry {
			get {
				return workersAvailable.Union(workersInUse).ToDictionary(p => p.Key, p => p.Value);
			}
		}

		public void BroadcastAnnouncePm(Uri newPuppetMasterUri) {
			if (newPuppetMasterUri.Host == Util.GetHostIpAddress() || newPuppetMasterUri == ServiceUrl) {
				//Trace.WriteLine("You are announcing yourself to yourself, please specify the "
				//	+ "target to be informed of your existence.");
				return;
			}

			lock (KnownPmsUris) {
				// if not known add it to known list
				if (!KnownPmsUris.Contains(newPuppetMasterUri))
					KnownPmsUris.Add(newPuppetMasterUri);

				var stateUpdate = true;
				while (stateUpdate) {
					stateUpdate = false;
					var knownCount = KnownPmsUris.Count;
					//Trace.WriteLine("Known PMs Count: " + knownCount);
					//Trace.WriteLine("Broadcasting known PMs list to known PMs.");

					KnownPmsUris.ForEach(uri => {
						var remotePM = RemotingHelper.GetRemoteObject<IPuppetMasterService>(uri);

						UpdatePmsList(remotePM.UpdatePmsList(new List<Uri>(KnownPmsUris) { ServiceUrl }));
						//Trace.WriteLine("Updating PM at '" + uri + "' of my known PMs.");

						if (knownCount < KnownPmsUris.Count) {
							//Trace.WriteLine("My known PMs list got updated when contacting PM at '" + uri + "'.");
							stateUpdate = true;
						}
					});

					//if (stateUpdate)
					//	Trace.WriteLine("Comunicating changes back again to all known PMs");
				}
			}
		}

		public List<Uri> UpdatePmsList(List<Uri> puppetMasterUrls) {
			var newPuppetMasters = (from pm in puppetMasterUrls
									where !KnownPmsUris.Contains(pm) && pm != ServiceUrl && pm.Host != ServiceUrl.Host
									select pm).ToList();

			if (newPuppetMasters.Count == 0)
				return KnownPmsUris;

			KnownPmsUris.AddRange(newPuppetMasters);
			newPuppetMasters.ForEach(uri => {
				Trace.WriteLine("New Puppet Master announced:" + uri);
			});
			return KnownPmsUris;
		}

		public void CreateWorker(int workerId, string serviceUrl, string entryUrl) {
			lock (globalLock) {
				var serviceUri = new Uri(serviceUrl);
				RemotingHelper.RegisterChannel(serviceUri);

				var remoteWorker = Worker.Run(workerId, serviceUri, GetServiceUri());
				workersAvailable.Add(remoteWorker.WorkerId, remoteWorker);
				remoteWorker.UpdateAvailableWorkers(workersAvailable.Values.ToList());

				Trace.WriteLine(string.Format("New worker created: id '{0}', url '{1}'."
					, workerId, serviceUri));

				if (!string.IsNullOrWhiteSpace(entryUrl))
					NotifyWorkerCreation(remoteWorker.ServiceUrl, entryUrl);
			}
		}

		public List<Uri> GetWorkersSharePm(Uri pmUri) {
			var share = new List<Uri>();
			BroadcastAnnouncePm(pmUri);

			lock (workerShareLock) {
				Trace.WriteLine("Get workers request from PuppetMaster : " + pmUri);
				var fairShare = FairScheduler();
				if (GetAvailableWorkers().Count >= FairScheduler()) {
					share = FairShareExecutor(fairShare);
				}
			}
			return share;
		}

		/// <summary>
		/// Gets the workers share, that corresponds to the number of workers that a given worker
		/// should receive in order to preserve cluster availability.
		/// </summary>
		/// <param name="taskRunnerUri">Uri of the taskRunner</param>
		/// <returns></returns>
		public List<Uri> GetWorkersShare(Uri taskRunnerUri) {
			var share = new List<Uri>();
			lock (workerShareLock) {
				var tr = RemotingHelper.GetRemoteObject<TaskRunner>(taskRunnerUri);
				if (tr == null)
					return share;
				EnsureRegistedTaskRunner(taskRunnerUri);
				var fairShare = FairScheduler();
				Trace.WriteLine("Get workers request from taskTracker : " + taskRunnerUri);

				if (GetAvailableWorkers().Count >= fairShare) {
					share = FairShareExecutor(fairShare);
				} else {
					share = GetRemoteWorkers(fairShare);
					if (share.Count > 0)
						return share;
					Trace.WriteLine("No workers available put JBTM in queue: " + taskRunnerUri);
					lock (jobTrackersQueueLock) {
						GetJobTrackersWaitingQueue().Enqueue(new Tuple<int, Uri>(fairShare, taskRunnerUri));
					}
				}
			}
			return share;
		}

		/// <summary>
		/// Gets needed workers from the PuppetMasters. If they aren't enough release workers from PuppetMasters.
		/// </summary>
		/// <param name="workersNeeded"></param>
		/// <returns></returns>
		private List<Uri> GetRemoteWorkers(int workersNeeded) {
			var remoteShare = new List<Uri>();
			lock (workersLock) {
				remoteShare = remoteShare.Concat(FairShareExecutor(GetAvailableWorkers().Count)).ToList();
				foreach (Uri pmUri in KnownPmsUris) {
					var pMaster = (IPuppetMasterService)Activator.GetObject(
						typeof(IPuppetMasterService),
						pmUri.ToString());
					var workers = pMaster.GetWorkersSharePm(ServiceUrl);
					if (workers == null)
						continue;
					remoteShare = remoteShare.Concat(workers).ToList();
					if (remoteShare.Count >= workersNeeded)
						break;
				}
				if (!(remoteShare.Count > workersNeeded)) {
					ReleaseWorkersOnPms(remoteShare);
					return new List<Uri>();
				}
			}
			return remoteShare;
		}

		private void ReleaseWorkersOnPms(List<Uri> remoteWorkers) {
			lock (workersLock) {
				foreach (var uri in remoteWorkers) {
					var wrk = RemotingHelper.GetRemoteObject<IWorker>(uri);
					var pmr = RemotingHelper.GetRemoteObject<IPuppetMasterService>(wrk.PuppetMasterUri);
					pmr.ReleaseWorkers(new List<int> { wrk.WorkerId });
				}
			}
		}

		private void EnsureRegistedTaskRunner(Uri trUri) {
			if (GetJobTrackersMaster().Contains(trUri))
				return;
			GetJobTrackersMaster().Add(trUri);
		}

		/// <summary>
		/// Gets a fair number of workers received.
		/// </summary>
		/// <param name="fairShare"></param>
		/// <returns></returns>
		private List<Uri> FairShareExecutor(int fairShare) {
			var filledShare = new List<Uri>();
			lock (workersLock) {
				for (var i = 0; i < fairShare; i++) {
					var worker = GetAvailableWorkers().Take(1).First().Value;
					GetAvailableWorkers().Remove(worker.WorkerId);
					filledShare.Add(worker.ServiceUrl);
					GetWorkersInUse().Add(worker.WorkerId, worker);
				}
			}
			return filledShare;
		}

		private void ProcessPendingShares() {
			lock (jobTrackersQueueLock) {
				if (!(GetJobTrackersWaitingQueue().Count > 0))
					return;
				while (true) {
					var workerTuple = GetJobTrackersWaitingQueue().Peek();
					lock (workersLock) {
						if (!(GetAvailableWorkers().Count > workerTuple.Item1))
							return;
						GetJobTrackersWaitingQueue().Dequeue();
						var tr = RemotingHelper.GetRemoteObject<TaskRunner>(workerTuple.Item2);
						if (tr == null) {
							GetJobTrackersWaitingQueue().Enqueue(workerTuple);
							continue;
						}
						tr.ReceiveShare(FairShareExecutor(workerTuple.Item1));
					}
				}
			}
		}

		/// <summary>
		/// Adds the received workers to the available list and removes them from workersInUse
		/// </summary>
		/// <param name="workersUsed"></param>
		public void ReleaseWorkers(List<int> workersUsed) {
			lock (workersLock) {
				foreach (var workerKey in workersUsed) {
					var worker = GetWorkersInUse()[workerKey];
					GetWorkersInUse().Remove(workerKey);
					GetAvailableWorkers().Add(workerKey, worker);
				}
			}
			ProcessPendingShares();
		}

		public int FairScheduler() {
			lock (workersLock) {
				var availableWorkers = GetAvailableWorkers().Count;
				var numOfJobTrackers = GetJobTrackersMaster().Count;

#if DEBUG
				// use this for testing purposes, this will force the system to fetch workers from
				// other puppet masters
				if (SYSTEMPARAM_SHARE > 0)
					return SYSTEMPARAM_SHARE;
#endif

				return
					Convert.ToInt32(Math.Ceiling((availableWorkers * 1.0) / ((numOfJobTrackers + KnownPmsUris.Count) * 1.0)));
			}
		}

		public void GetStatus() {
			Trace.WriteLine("PuppetMaster [ID: " + ServiceName + "] - Running: '" + ServiceUrl + "'.");
			foreach (var worker in workersAvailable.Values) {
				var remoteWorker = RemotingHelper.GetRemoteObject<IWorker>(worker.ServiceUrl);
				remoteWorker.GetStatus();
			}
		}

		public Queue<Tuple<int, Uri>> GetJobTrackersWaitingQueue() {
			return jobTrackersWaitingQueue;
		}

		public void ReleaseWorker(Uri workerServiceUri) {
			var wrk = RemotingHelper.GetRemoteObject<IWorker>(workerServiceUri);
			lock (workersLock) {
				GetWorkersInUse().Remove(wrk.WorkerId);
				GetAvailableWorkers().Add(wrk.WorkerId, wrk);
			}
			ProcessPendingShares();
		}

		public Dictionary<int, IWorker> GetAvailableWorkers() {
			return workersAvailable;
		}

		public List<Uri> GetJobTrackersMaster() {
			return jobTrackersMaster;
		}

		public int GetJobTrackersMasterCount() {
			if (jobTrackersMaster == null)
				return 0;
			return jobTrackersMaster.Count;
		}

		public Dictionary<int, IWorker> GetWorkersInUse() {
			return workersInUse;
		}

		public void SlowWorker(int workerId, int seconds) {
			IWorker worker;

			try {
				worker = WorkersRegistry[workerId];
			} catch (System.Exception e) {
				throw new InvalidWorkerIdException(workerId, e.Message);
			}

			var remoteWorker = RemotingHelper.GetRemoteObject<IWorker>(worker.ServiceUrl);
			remoteWorker.Slow(seconds);
		}

		public void FreezeWorker(int workerId) {
			IWorker worker;

			try {
				worker = WorkersRegistry[workerId];
			} catch (System.Exception e) {
				throw new InvalidWorkerIdException(workerId, e.Message);
			}

			var remoteWorker = RemotingHelper.GetRemoteObject<IWorker>(worker.ServiceUrl);
			remoteWorker.Freeze();
		}

		public void UnfreezeWorker(int workerId) {
			IWorker worker;

			try {
				worker = WorkersRegistry[workerId];
			} catch (System.Exception e) {
				throw new InvalidWorkerIdException(workerId, e.Message);
			}

			var remoteWorker = RemotingHelper.GetRemoteObject<IWorker>(worker.ServiceUrl);
			remoteWorker.UnFreeze();
		}

		public void FreezeCommunication(int workerId) {
			IWorker worker;

			try {
				worker = WorkersRegistry[workerId];
			} catch (System.Exception e) {
				throw new InvalidWorkerIdException(workerId, e.Message);
			}

			var remoteWorker = RemotingHelper.GetRemoteObject<IWorker>(worker.ServiceUrl);
			remoteWorker.FreezeCommunication();
		}

		public void UnfreezeCommunication(int workerId) {
			IWorker worker;

			try {
				worker = WorkersRegistry[workerId];
			} catch (System.Exception e) {
				throw new InvalidWorkerIdException(workerId, e.Message);
			}

			var remoteWorker = RemotingHelper.GetRemoteObject<IWorker>(worker.ServiceUrl);
			remoteWorker.UnfreezeCommunication();
		}

		private static void NotifyWorkerCreation(Uri workerServiceUri, String entryUrl) {
			//Trace.WriteLine("Sends notification to worker at ENTRY_URL informing worker creation.");

			var masterWorker = RemotingHelper.GetRemoteObject<IWorker>(entryUrl);
			masterWorker.NotifyWorkerJoin(workerServiceUri);
		}

		/// <summary>
		/// Serves a Marshalled Puppet Master object at a specific IChannel under ChannelServices.
		/// </summary>
		public static void Run() {
			RemotingHelper.RegisterChannel(ServiceUrl);
			RemotingHelper.CreateWellKnownService(typeof(PuppetMasterService), ServiceName);
			Trace.WriteLine("Puppet Master Service endpoint ready at '" + ServiceUrl + "'");
		}
	}
}
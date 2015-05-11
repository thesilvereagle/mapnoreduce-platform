﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting;
using System.Threading;
using SharedTypes;

namespace PlatformCore
{
	[Serializable]
	public class Worker : MarshalByRefObject, IWorker
	{
		public const int NOTIFY_TIMEOUT = 1000 * 5;

		private readonly object workerMutex = new object();
		private readonly object workerReceiveJobLock = new object();
		private JobTracker taskTracker = null;
		private JobTracker masterTracker = null;
		private readonly List<ManualResetEvent> frozenRequests = new List<ManualResetEvent>();
		/// <summary>
		/// List of all workers known by this worker.
		/// </summary>
		private Dictionary<int /*worker id*/, IWorker> workersList;

		public enum State { Running, Failed, Frozen };
		public delegate bool ExecuteMapJobDelegate(JobTask task);
		/// <summary>
		/// This worker id.
		/// </summary>
		public int WorkerId { get; set; }
		/// <summary>
		/// The service URL used to reach this work remotely.
		/// </summary>
		public Uri ServiceUrl { get; set; }
		public Uri PuppetMasterUri { get; set; }
		public WorkerStatus Status { get; set; }

		public Worker() {
			Status = WorkerStatus.Available;
		}

		public Worker(int workerId, Uri serviceUrl, Dictionary<int, IWorker> availableWorkers, Uri puppetMasterServiceUri)
			: this() {
			WorkerId = workerId;
			ServiceUrl = serviceUrl;
			workersList = new Dictionary<int, IWorker>(availableWorkers);
			PuppetMasterUri = puppetMasterServiceUri;
		}

		/// <summary>
		/// Overrides the default object leasing behavior such that the object is kept in memory as
		/// long as the host application domain is running.
		/// </summary>
		/// <see><cref>https://msdn.microsoft.com/en-us/library/ms973841.aspx</cref></see>
		public override object InitializeLifetimeService() {
			return null;
		}

		public void UpdateAvailableWorkers(Dictionary<int, IWorker> availableWorkers) {
			StateCheck();
			lock (workerMutex) {
				workersList = new Dictionary<int, IWorker>(availableWorkers);
			}
		}

		public void NotifyWorkerJoin(Uri serviceUri) {
			StateCheck();
			var workerToAdd = RemotingHelper.GetRemoteObject<IWorker>(serviceUri);
			lock (workerMutex) {
				try {
					workersList.Add(workerToAdd.WorkerId, workerToAdd);

				} catch (Exception e) {
					Trace.WriteLine(e.Message);
				}
			}
		}

		/// <summary>
		/// Wakes requests frozen during frozen state.
		/// </summary>
		/// <returns></returns>
		private bool ProcessFrozenRequests() {
			foreach (var mre in frozenRequests) {
				mre.Set();
			}
			return true;
		}

		/// <summary>
		/// Puts to sleep all incoming requests while worker is frozen.
		/// </summary>
		private void StateCheck() {
			WorkerStatus status;
			lock (workerMutex) {
				status = Status;
			}

			switch (status) {
				case WorkerStatus.Offline:
					throw new RemotingException("Server is Offline;");
				case WorkerStatus.Frozen:
					var mre = new ManualResetEvent(false);
					frozenRequests.Add(mre);
					mre.WaitOne();
					break;
			}
		}

		private void EnsureMasterTracker() {
			if (masterTracker != null)
				return;
			masterTracker = new TaskRunner(this);
			new Thread(() => { masterTracker.Run(); }).Start();
		}

		private void EnsureTaskTracker() {
			if (taskTracker != null)
				return;
			taskTracker = new TaskTracker(this);
			new Thread(() => { taskTracker.Run(); }).Start();
		}

		public Dictionary<int, IWorker> GetWorkersList() {
			lock (workerMutex) {
				return workersList;
			}
		}

		public WorkerStatus GetStatus() {
			lock (workerMutex) {
				Trace.WriteLine("Worker [ID: " + WorkerId + "] - Status: '" + Status + "'.");
				return Status;
			}
		}

		public void SetStatus(WorkerStatus status) {
			lock (workerMutex) {
				Status = status;
			}
		}

		/// <summary>
		/// Receives a Map job and distributes the processing of their contents accross multiple
		/// workers. Each worker processes a split of the given filePath (used as file ID), calling
		/// the Map method of the received class name of the assembly code.
		/// </summary>
		/// <param name="task">The job to be processed.</param>
		public void ReceiveMapJob(IJobTask task) {
			StateCheck();
			lock (workerReceiveJobLock) {
				EnsureMasterTracker();

				Trace.WriteLine("New map job received by worker [ID: " + WorkerId + "].\n"
					+ "Master Job Tracker Uri: '" + masterTracker.ServiceUri + "'");

				task.JobTrackerUri = masterTracker.ServiceUri;
				masterTracker.ScheduleJob(task);
				PullAvailableWorkers();
				masterTracker.Wake();
			}
		}

		private void PullAvailableWorkers() {
			var pMaster = (IPuppetMasterService)Activator.GetObject(
				typeof(IPuppetMasterService),
				PuppetMasterUri.ToString());
			try {
				UpdateAvailableWorkers(pMaster.GetWorkers());
			} catch (Exception e) {
				Trace.WriteLine(e.Message);
			}
		}

		public void ExecuteMapJob(int split,
				string fileName, List<int> fileSplits, Uri jobTrackerUri, string mapClassName,
				byte[] mapFunctionName, string outputReceiverUrl, string splitProviderUrl) {

			var newTask = new JobTask {
				FileName = fileName,
				SplitNumber = split,
				FileSplits = fileSplits,
				JobTrackerUri = jobTrackerUri,
				MapClassName = mapClassName,
				MapFunctionAssembly = mapFunctionName,
				OutputReceiverUrl = outputReceiverUrl,
				SplitProviderUrl = splitProviderUrl
			};

			Trace.WriteLine("Executing job on worker [ID: " + WorkerId + "].");
			ExecuteMapJob(newTask);
		}

		public void ReceiveJobTrackerState(JobTrackerStateInfo getState) {
			//TODO: create job tracker slave
		}

		public void ExecuteMapJob(IJobTask task) {
			StateCheck();
			lock (workerMutex) {
				EnsureTaskTracker();
				taskTracker.ScheduleJob(task);
				Status = WorkerStatus.Busy;
			}

#if DEBUG
			/*work delay simulation*/
			// Thread.Sleep(15000);
			StateCheck();
#endif

			try {
				var splitProvider = (IClientSplitProviderService)Activator.GetObject(
					typeof(IClientSplitProviderService),
					task.SplitProviderUrl);

				var data = splitProvider.GetFileSplit(task.FileName, task.SplitNumber);
				var assembly = Assembly.Load(task.MapFunctionAssembly);

				foreach (var type in assembly.GetTypes()) {
					if (!type.IsClass || !type.FullName.EndsWith("." + task.MapClassName, StringComparison.InvariantCultureIgnoreCase))
						continue;

					var mapperClassObj = Activator.CreateInstance(type);
					object[] args = { data };

					var result = (IList<KeyValuePair<string, string>>)type.InvokeMember("Map",
						BindingFlags.Default | BindingFlags.InvokeMethod, null, mapperClassObj, args);

					Trace.WriteLine("MAPPER: Map call executed with success!");

					var outputReceiver = (IClientOutputReceiverService)Activator.GetObject(
						typeof(IClientOutputReceiverService),
						task.OutputReceiverUrl);

					outputReceiver.ReceiveMapOutputFragment(
						task.FileName
						, (from r in result select r.Key + " " + r.Value).ToArray()
						, task.SplitNumber);

					return;
				}
			} finally {
				Trace.WriteLine("Send alives thread finished on worker [ID: '" + WorkerId + "'].");
				lock (workerMutex) {
					Status = WorkerStatus.Available;
				}
			}
		}

		internal static Worker Run(int workerId, Uri serviceUrl, Dictionary<int, IWorker> workers, Uri puppetMasterServiceUri) {
			var wrk = new Worker(workerId, serviceUrl, workers, puppetMasterServiceUri);
			RemotingHelper.CreateService(wrk, serviceUrl);
			return wrk;
		}

		public void Slow(int secs) {
			StateCheck();
			Thread.Sleep(secs * 1000);
		}

		public void Freeze() {
			WorkerStatus status;
			lock (workerMutex) {
				status = Status;
			}
			if (status == WorkerStatus.Frozen)
				return;

			lock (workerMutex) {
				Status = WorkerStatus.Frozen;
			}
			frozenRequests.Clear();
		}

		public void UnFreeze() {
			WorkerStatus status;
			lock (workerMutex) {
				status = Status;
			}
			if (status != WorkerStatus.Offline && status != WorkerStatus.Frozen)
				return;
			lock (workerMutex) {
				Status = WorkerStatus.Available;
			}
			ProcessFrozenRequests();
		}

		public void FreezeCommunication() {
			StateCheck();
			lock (workerMutex) {
				masterTracker.FreezeCommunication();
			}
		}

		public void UnfreezeCommunication() {
			StateCheck();
			lock (workerMutex) {
				masterTracker.UnfreezeCommunication();
			}
		}
	}
}
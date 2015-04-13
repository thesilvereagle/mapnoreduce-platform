﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.Remoting;
using System.Threading;
using SharedTypes;

namespace PlatformCore
{
	public class JobTracker : MarshalByRefObject, IJobTracker
	{
		private const int NOTIFY_TIMEOUT = 1000 * 5;
		private const int PROCESSING_DELAY_TIMEOUT = 2000;
		private readonly object trackerMutex = new object();
		private readonly Worker worker;
		private readonly Queue<IJobTask> jobQueue = new Queue<IJobTask>();
		private readonly Dictionary</*splitnumber*/ int, /*workerid*/ int> splitsBeingProcessed = new Dictionary<int, int>();
		private readonly Dictionary</*workerid*/ int, /*lastupdate*/DateTime> workerAliveSignals = new Dictionary<int, DateTime>();
		/// <summary>
		/// The job splits priority queue.
		/// </summary>
		private Queue<int> splitsQueue;

		public IJobTask CurrentJob { get; private set; }
		public Uri ServiceUri { get; set; }
		public JobTrackerMode Mode { get; set; }
		public JobTrackerState Status { get; set; }

		public JobTracker() {
			Mode = JobTrackerMode.Passive;
			Status = JobTrackerState.Available;
		}

		public JobTracker(Worker worker, JobTrackerMode trackerMode)
			: this() {
			this.worker = worker;
			Mode = trackerMode;
		}

		[SuppressMessage("ReSharper", "FunctionNeverReturns")]
		public void Start() {
			switch (Mode) {
				case JobTrackerMode.Active:
					ServiceUri = new Uri("tcp://localhost:" + worker.ServiceUrl.Port + "MasterJobTracker-W" + worker.WorkerId);
					RemotingServices.Marshal(this, "MasterJobTracker-W" + worker.WorkerId, typeof(IJobTracker));
					while (true) {
						MasterTrackerMain();
					}
				case JobTrackerMode.Passive:
					ServiceUri = new Uri("tcp://localhost:" + worker.ServiceUrl.Port + "SlaveJobTracker-W" + worker.WorkerId);
					RemotingServices.Marshal(this, "SlaveJobTracker-W" + worker.WorkerId, typeof(IJobTracker));
					while (true) {
						SlaveTrackerMain();
					}
			}
		}

		private void MasterTrackerMain() {
			lock (trackerMutex) {
				// If job tracker busy or without jobs to process exit.
				if (!(jobQueue.Count > 0) || Status != JobTrackerState.Available)
					return;

				// Get next job and set state to busy.
				CurrentJob = jobQueue.Dequeue();
				splitsQueue = new Queue<int>(CurrentJob.FileSplits);
				Status = JobTrackerState.Busy;
			}

			var thrMonitor = new Thread(MonitorSplitProcessing);
			thrMonitor.Start();

			// Loops until all splits are processed, i.e, job done.
			while (splitsQueue.Count > 0 || splitsBeingProcessed.Count != 0) {
				// Selects from all online workers, those that are not busy.
				var availableWorkers = new Queue<IWorker>((
						from w in worker.WorkersList
						where w.Value.GetStatus() == WorkerStatus.Available
						select w.Value
					).ToList());

				SplitsDelivery(availableWorkers, CurrentJob);
				Thread.Sleep(PROCESSING_DELAY_TIMEOUT);
			}

			lock (trackerMutex) {
				CurrentJob = null;
				splitsBeingProcessed.Clear();
				splitsQueue.Clear();
			}

			thrMonitor.Join();
		}

		private void SlaveTrackerMain() {
			IJobTask currentJob = null;

			// Updates slave job tracker shared state.
			lock (trackerMutex) {
				if (jobQueue.Count > 0) {
					currentJob = CurrentJob = jobQueue.Dequeue();
					Status = JobTrackerState.Busy;
				} else
					Status = JobTrackerState.Available;
			}

			if (currentJob == null)
				return;

			while (worker.GetStatus() == WorkerStatus.Busy) {
				var masterTracker = RemotingHelper.GetRemoteObject<IJobTracker>(currentJob.JobTrackerUri);
				masterTracker.Alive(worker.WorkerId);
				Thread.Sleep(NOTIFY_TIMEOUT);
			}
		}

		private void SplitsDelivery(Queue<IWorker> availableWorkers, IJobTask job) {
			// Delivers as many splits as it cans, considering the number of available workers.
			for (var i = 0; i < Math.Min(availableWorkers.Count, splitsQueue.Count); i++) {
				var remoteWorker = availableWorkers.Dequeue();
				var split = splitsQueue.Peek();

				try {
					// The callback called after the execution of the async method call.
					var callback = new AsyncCallback(result => {
						Trace.WriteLine(string.Format("Worker '{0}' finished processing split number '{1}'."
							, remoteWorker.ServiceUrl, split));
					});

					// Async call to ExecuteMapJob.
					Trace.WriteLine(string.Format("Job split {0} sent to worker at '{1}'.", job.SplitNumber, remoteWorker.ServiceUrl));
					remoteWorker.AsyncExecuteMapJob(this, split, remoteWorker, callback, job);

					splitsQueue.Dequeue();
					Trace.WriteLine("Split " + job.SplitNumber + " removed from splits queue.");

					workerAliveSignals[remoteWorker.WorkerId] = DateTime.Now;
					splitsBeingProcessed.Add(split, remoteWorker.WorkerId);
				} catch (RemotingException ex) {
					Trace.WriteLine(ex.GetType().FullName + " - " + ex.Message
						+ " -->> " + ex.StackTrace);
				}
			}
		}

		private void MonitorSplitProcessing() {
			while (true) {
				lock (trackerMutex) {
					if (CurrentJob == null)
						return;
				}

				foreach (var keyValue in splitsBeingProcessed) {
					var workerId = keyValue.Value;
					var split = keyValue.Key;
					TimeSpan tspan;

					lock (trackerMutex) {
						tspan = DateTime.Now.Subtract(workerAliveSignals[workerId]);
					}

					if (!(tspan.TotalSeconds > NOTIFY_TIMEOUT * 3))
						continue;

					lock (trackerMutex) {
						// Worker not responding.
						worker.Status = WorkerStatus.Offline;
						splitsQueue.Enqueue(split);
						splitsBeingProcessed.Remove(split);
					}
				}

				Thread.Sleep(NOTIFY_TIMEOUT);
			}
		}

		public void Alive(int wid) {
			lock (trackerMutex) {
				var w = ((Worker)worker.WorkersList[wid]);
				if (w.Status == WorkerStatus.Offline)
					w.Status = WorkerStatus.Available;
				workerAliveSignals[wid] = DateTime.Now;
			}
		}

		public void FreezeCommunication() {
			var dte = new EnvDTE.DTE();
			var thread = dte.Debugger.CurrentThread;
			thread.Freeze();
		}

		public void UnfreezeCommunication() {
			var dte = new EnvDTE.DTE();
			var thread = dte.Debugger.CurrentThread;
			thread.Thaw();
		}

		public void ScheduleJob(IJobTask job) {
			lock (trackerMutex) {
				jobQueue.Enqueue(job);
			}
		}
	}
}
﻿using System;
using System.Collections.Generic;

namespace SharedTypes
{
    public interface IPuppetMasterService
    {
        void CreateWorker(int workerId, string serviceUrl, string entryUrl);

        void GetStatus();

        void SlowWorker(int workerId, int seconds);

        void FreezeWorker(int workerId);

        void UnfreezeWorker(int workerId);

        void FreezeCommunication(int workerId);

        void UnfreezeCommunication(int workerId);

        void ReleaseWorker(Uri workerServiceUri);

        Dictionary<int, IWorker> GetAvailableWorkers();

		/// <summary>
		/// Gets the workers share, that corresponds to the number of workers that a given worker should receive in order to 
		/// preserve cluster availability.
		/// </summary>
		/// <param name="taskRunnerUri">Uri of the taskRunner</param>
		/// <returns></returns>
		List<Uri> GetWorkersShare(Uri taskRunnerUri);

        void ReleaseWorkers(List<int> workersUsed);

        Uri GetServiceUri();

        void BroadcastAnnouncePm(Uri newPuppetMasterUri);

        List<Uri> GetJobTrackersMaster();

        int GetJobTrackersMasterCount();

        List<Uri> UpdatePmsList(List<Uri> puppetMasterUrls);

        List<Uri> GetWorkersSharePm(Uri pmUri);
    }
}
﻿using System;
using System.Collections.Generic;

namespace SharedTypes
{
	public interface IPuppetMasterService
	{
		void CreateWorker(int workerId, string serviceUrl, string entryUrl);

		void GetStatus();

		void Wait(int seconds);

		void SlowWorker(int workerId, int seconds);

		void FreezeWorker(int workerId);

		void UnfreezeWorker(int workerId);

		void FreezeCommunication(int workerId);

		void UnfreezeCommunication(int workerId);

		Dictionary<int, IWorker> GetWorkers();

		Uri GetServiceUri();
	}
}
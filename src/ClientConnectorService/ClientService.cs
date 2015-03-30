﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Tcp;
using System.Runtime.Serialization.Formatters;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharedTypes;

namespace ClientServices
{
    public class ClientService : MarshalByRefObject, IClientService
    {
        public const int CLIENT_CHANNEL_PORT = 8090;
        public const string CLIENT_OUTPUTRECV_SVCNAME = "MNRP-ClientORS";
        public const string CLIENT_SPLITPROV_SVCNAME = "MNRP-ClientSPS";

        private ClientOutputReceiverService corSvc;
        private ClientSplitProviderService cspSvc;
        public string EntryUrl { get; set; }
        public bool IsStarted { get; private set; }

        public string GetOutputReceiverServiceUrl() {
            return corSvc.ServiceURL;
        }

        public string GetSplitProviderServiceUrl() {
            return cspSvc.ServiceURL;
        }

        public void Init(string entryUrl) {
            if (IsStarted)
                return;

            EntryUrl = entryUrl;
            var provider = new BinaryServerFormatterSinkProvider {
                TypeFilterLevel = TypeFilterLevel.Full
            };

            IDictionary props = new Hashtable();
            props["port"] = CLIENT_CHANNEL_PORT;

            var channel = new TcpChannel(props, null, provider);
            ChannelServices.RegisterChannel(channel, true);

            corSvc = new ClientOutputReceiverService(
                string.Format("tcp://localhost:{0}/{1}", CLIENT_CHANNEL_PORT, CLIENT_OUTPUTRECV_SVCNAME));
            cspSvc = new ClientSplitProviderService(
                string.Format("tcp://localhost:{0}/{1}", CLIENT_CHANNEL_PORT, CLIENT_SPLITPROV_SVCNAME));

            IsStarted = true;
            RemotingServices.Marshal(corSvc, CLIENT_OUTPUTRECV_SVCNAME, typeof(ClientOutputReceiverService));
            RemotingServices.Marshal(cspSvc, CLIENT_SPLITPROV_SVCNAME, typeof(ClientSplitProviderService));

            Debug.WriteLine("Client Output Receiver Service, available at {0}.", corSvc.ServiceURL);
            Debug.WriteLine("Client Split Provider Service, available at {0}.", cspSvc.ServiceURL);
        }


        public void Submit(string filePath, int nSplits, string outputDir, string mapClassName, string assemblyFilePath) {
            cspSvc.SplitAndSave(filePath, nSplits);

            byte[] mapAssemblyCode = File.ReadAllBytes(assemblyFilePath);
            var masterWorker = RemotingHelper.GetRemoteObject<IWorker>(EntryUrl);
            masterWorker.ReceiveMapJob(filePath, nSplits, mapAssemblyCode, mapClassName);

            while (!corSvc.IsMapResultReady(filePath, nSplits)) {
                Thread.Sleep(5000);
            }

            var result = corSvc.GetMapResult();
            using (var outFile = File.CreateText(Path.Combine(outputDir, filePath + ".out"))) {
                outFile.Write(String.Join("\n", result));
            }
        }
    }
}
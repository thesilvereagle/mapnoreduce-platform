﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PlatformCore.Exceptions
{
    public class InvalidWorkerServiceUrlException : Exception
    {
        public InvalidWorkerServiceUrlException(int workerId, string serviceURL)
            : base(string.Format("The service URL '{0}' for worker '{1}' is invalid.",
                serviceURL, workerId)) { }
    }
}
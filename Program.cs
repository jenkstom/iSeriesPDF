using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using lpd;

namespace cli_rqsvc
{
    class Program
    {
        public static int Main(String[] args)
        {
            lpd.lpdaemon.StartLPD();
            Console.ReadLine();
            lpd.lpdaemon.StopLPD();
            return 0;
        }
    }
}

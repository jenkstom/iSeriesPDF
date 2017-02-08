using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using System.Threading;


namespace lpd
{
    // Thread signal.  
    class lpdaemon
    {
        public static ManualResetEvent allDone = new ManualResetEvent(false);
        private static List<StateObject> states = new List<StateObject>();

        public static String path = "";
        public static String smtpserver = "";
        public static String origdomain = "";

        private static Timer timeout;

        public class StateObject
        {
            // Client  socket.  
            public Socket workSocket = null;
            // Size of receive buffer.  
            public const int BufferSize = 2048;
            // Receive buffer.  
            public byte[] buffer = new byte[BufferSize];
            // Received data info  
            public String cfName = "";
            public StringBuilder cf = new StringBuilder();
            public bool rcvcf = false;
            public String dfName = "";
            public StringBuilder df = new StringBuilder();
            public bool rcvdf = false;
            public String queuename = "";

            //buffer for partial commands
            public StringBuilder cb = new StringBuilder();

            public Int32 bytesremaining = 0;
            public DateTime lastPacket;
            public bool lprmode = false;
        }


        public static void StartLPD()
        {
            log("Starting");
            log("THIS IS A SERVER, PLEASE DO NOT CLOSE THE WINDOW.");
            //**TODO: add smtp user and pass
            path = ConfigurationManager.AppSettings["spoolpath"];
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            smtpserver = ConfigurationManager.AppSettings["smtp"];
            origdomain = ConfigurationManager.AppSettings["origdomain"];
            delfiles = Convert.ToBoolean(ConfigurationManager.AppSettings["delfiles"]);
            log($"Spoolpath: {path}");
            log($"SMTP: {smtpserver}");
            log($"Domain: {origdomain}");
            log($"Delete Files: {delfiles}");

            // Data buffer for incoming data.  
            byte[] bytes = new Byte[2048];

            timeout = new Timer(timeoutProcessor,null,0,5000);

            // Establish the local endpoint for the socket.  
            // The DNS name of the computer  
            // running the listener is "host.contoso.com".  
            IPHostEntry ipHostInfo = Dns.Resolve(Dns.GetHostName());
            IPAddress ipAddress = ipHostInfo.AddressList[0];
            IPEndPoint localEndPoint = new IPEndPoint(ipAddress, 515);

            // Create a TCP/IP socket.  
            Socket listener = new Socket(AddressFamily.InterNetwork,
                SocketType.Stream, ProtocolType.Tcp);

            // Bind the socket to the local endpoint and listen for incoming connections.  
            try
            {
                listener.Bind(localEndPoint);
                listener.Listen(100);

                while (true)
                {
                    // Set the event to nonsignaled state.  
                    allDone.Reset();

                    // Start an asynchronous socket to listen for connections.  
                    //log("Waiting for a connection...");
                    listener.BeginAccept(
                        new AsyncCallback(AcceptCallback),
                        listener);

                    // Wait until a connection is made before continuing.  
                    allDone.WaitOne();
                }

            }
            catch (Exception e)
            {
                log(e.ToString());
            }
        }

        public static void StopLPD()
        {
            log("Shutting down");
            foreach (StateObject state in states)
            {
                state.workSocket.Shutdown(SocketShutdown.Both);
                state.workSocket.Close();
            }

            //serverSocket.Close();
            //todo - kill listener somehow
        }

        private static void log(String x)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss}: {x}");
        }

        private static void timeoutProcessor(object state)
        {
            List<StateObject> deleteList = new List<StateObject>();
            foreach (StateObject s in states)
            {
                if (s.lastPacket < DateTime.Now.AddSeconds(-10))
                {
                    try
                    {
                        log($"Killing socket from {IPAddress.Parse(((IPEndPoint)s.workSocket.RemoteEndPoint).Address.ToString())} " +
                            $" port {((IPEndPoint)s.workSocket.RemoteEndPoint).Port.ToString()} due to timeout.");
                        s.workSocket.Shutdown(SocketShutdown.Both);
                        s.workSocket.Close();
                    }
                    catch { }
                    deleteList.Add(s);
                }
            }

            foreach (StateObject d in deleteList)
                states.Remove(d);
        }

        public static void AcceptCallback(IAsyncResult ar)
        {
            // Signal the main thread to continue.  
            allDone.Set();

            // Get the socket that handles the client request.  
            Socket listener = (Socket)ar.AsyncState;
            Socket handler = listener.EndAccept(ar);

            log($"Connection from {IPAddress.Parse(((IPEndPoint)handler.RemoteEndPoint).Address.ToString())} " +
                $" port {((IPEndPoint)handler.RemoteEndPoint).Port.ToString()}");

            // Create the state object.  
            StateObject state = new StateObject();
            state.workSocket = handler;
            state.lastPacket = DateTime.Now;
            states.Add(state);
            handler.BeginReceive(state.buffer, 0, StateObject.BufferSize, 0,
                new AsyncCallback(ReadCallback), state);
        }

        public static void ReadCallback(IAsyncResult ar)
        {
            try
            {
                String content = String.Empty;

                // Retrieve the state object and the handler socket  
                // from the asynchronous state object.  
                StateObject state = (StateObject)ar.AsyncState;
                Socket handler = state.workSocket;
                state.lastPacket = DateTime.Now;

                // Read data from the client socket.   
                int bytesRead = handler.EndReceive(ar);

                StringBuilder sb = null;
                if (bytesRead > 0)
                {
                    if (state.bytesremaining > 0)
                    {
                        if (state.rcvcf)
                            sb = state.cf;
                        else if (state.rcvdf)
                            sb = state.df;
                        if (sb != null)
                        {
                            if (bytesRead >= state.bytesremaining)
                            {
                                sb.Append(Encoding.ASCII.GetString(
                                    state.buffer, 0, state.bytesremaining));

                                //remove bytes that go to file from remaining bytes
                                byte[] temp = new byte[StateObject.BufferSize];
                                Array.Copy(state.buffer, state.bytesremaining, temp, 0, bytesRead - state.bytesremaining);
                                Array.Copy(temp, state.buffer, bytesRead - state.bytesremaining);
                                bytesRead -= state.bytesremaining;

                                state.bytesremaining = 0;
                                SendAck(state.workSocket);

                                if (state.rcvcf)
                                {
                                    state.rcvcf = false;
                                    //state.lprmode = false;
                                    if (doConversion(state)) return;
                                }
                                if (state.rcvdf)
                                {
                                    state.rcvdf = false;
                                    //state.lprmode = false;
                                    if (doConversion(state)) return;
                                }
                                //send ack for LPD protocol - not sure if this is necessary
                                //SendAck(state.workSocket);
                            }
                            else
                            {
                                sb.Append(Encoding.ASCII.GetString(
                                    state.buffer, 0, bytesRead));
                                state.bytesremaining -= bytesRead;
                            }

                        }

                        //command section
                    } //if bytesremaining>0
                    else if (bytesRead > 0)
                    {
                        //All commands end with linefeed. If there's not 
                        //one, store the current buffer and wait for more data
                        state.cb.Append(Encoding.ASCII.GetString(
                            state.buffer, 0, bytesRead));
                        if (state.cb.ToString().IndexOf(Convert.ToChar(10)) >= 0)
                            processCommand(state);

                    }


                    // Not all data received. Get more.  
                    handler.BeginReceive(state.buffer, 0, StateObject.BufferSize, 0,
                    new AsyncCallback(ReadCallback), state);
                }
                else //remote closed connection, kill socket
                {
                    //state.workSocket.Shutdown(SocketShutdown.Both);
                    //state.workSocket.Close();
                    //states.Remove(state);
                }
            }
            catch (Exception ex)
            {
                log(ex.Message);
            }
        }

        private static void processCommand(StateObject state)
        {
            //Process the command buffer in the state object, remove lines as they are processed
            String cmdbuf = state.cb.ToString();
            String cmd;
            String filebuff;
            Int32 length;
            String filename;

            //foreach (String cmd in cmdarray)
            while (cmdbuf.Length > 0 && cmdbuf.IndexOf(Convert.ToChar(10)) >= 0)
            {
                cmd = (cmdbuf.Substring(0, cmdbuf.IndexOf(Convert.ToChar(10))));
                //remove the processed command from the command buffer
                state.cb.Remove(0, cmd.Length + 1);
                cmdbuf = state.cb.ToString();

                if (!state.lprmode)
                {
                    //Command mode
                    switch (Convert.ToByte(cmd[0]))
                    {
                        case 2: //lpr
                            state.queuename = cmd.Substring(1);
                            state.lprmode = true;
                            log($"Received LPR Command for queue {state.queuename}");
                            SendAck(state.workSocket);
                            break;
                        default:
                            Send(state.workSocket, "OK \n");
                            break;
                    }
                }
                else
                {
                    length = Convert.ToInt32(cmd.Substring(1, cmd.IndexOf(' ') - 1)) + 1;
                    filename = cmd.Substring(cmd.IndexOf(' ') + 1);
                    switch (Convert.ToByte(cmd[0]))
                    {
                        //subcommand mode
                        case 2: //control file
                            //log("Receiving control file");
                            state.cfName = filename;
                            if (cmdbuf.Length > length)
                            {
                                state.cf = new StringBuilder(cmdbuf.Substring(0, length));
                                state.cb.Remove(0, length);
                                cmdbuf = state.cb.ToString();
                                state.bytesremaining = 0;
                                state.rcvcf = false;
                                //state.lprmode = false;
                            }
                            else
                            {
                                state.cf = new StringBuilder(cmdbuf);
                                state.cb.Clear();
                                state.bytesremaining = length - cmdbuf.Length;
                                cmdbuf = "";
                                state.rcvcf = true;
                            }
                            SendAck(state.workSocket);
                            break;
                        case 3: //data file
                            //log("Receiving control file");
                            state.dfName = filename;
                            if (cmdbuf.Length > length)
                            {
                                state.df = new StringBuilder(cmdbuf.Substring(0, length));
                                state.cb.Remove(0, length);
                                cmdbuf = state.cb.ToString();
                                state.bytesremaining = 0;
                                state.rcvdf = false;
                                //state.lprmode = false;
                            }
                            else
                            {
                                state.df = new StringBuilder(cmdbuf);
                                state.cb.Clear();
                                state.bytesremaining = length - cmdbuf.Length;
                                cmdbuf = "";
                                state.rcvdf = true;
                            }
                            SendAck(state.workSocket);
                            break;
                        default:
                            Send(state.workSocket, "OK \n");
                            break;
                    }
                }
            }

            //if df and cf are set and bytes remaining is zero, then process the file
            if (state.df.Length > 0 && state.cf.Length > 0 && state.bytesremaining == 0)
            {
                doConversion(state);
            }
        }

        private static bool doConversion(StateObject state)
        {
            if (state.rcvcf == false && state.rcvdf == false && state.cf.Length != 0
                && state.df.Length != 0 && state.queuename != "" && state.cf.Length > 0
                && state.df.Length > 0)
            {
                state.workSocket.Shutdown(SocketShutdown.Both);
                state.workSocket.Close();

                String bfn = Path.GetFileNameWithoutExtension(state.dfName);
                String username = "";
                String jobname = "";

                //remove null characters at end of files
                state.cf.Remove(state.cf.Length-1, 1);
                state.df.Remove(state.df.Length-1, 1);

                string[] lines = new StringReader(state.cf.ToString()).ReadToEnd().Split(Convert.ToChar(10));
                foreach (String l in lines)
                {
                    if (l.Length > 0)
                    {
                        if (l.Substring(0, 1) == "P") username = l.Substring(1);
                        if (l.Substring(0, 1) == "N") jobname = l.Substring(1);
                    }
                }

                if (username == "")
                {
                    log("Can't determine username, aborting.");
                    return true;
                }

                //log($"Processing received file {bfn}, user {username}, queue {state.queuename}");

                StreamWriter datafile = new StreamWriter(Path.Combine(path, state.dfName));
                datafile.Write(state.df.ToString());
                datafile.Flush();
                datafile.Close();

                String destExt = "";
                String outFormat = "";
                bool multiImage = false;

                switch (state.queuename)
                {
                    case "PDFEMAIL":
                        destExt = ".PDF";
                        multiImage = false;
                        outFormat = "pdfwrite";
                        break;
                    case "IMAGING":
                        destExt = "%d.tif";
                        multiImage = true;
                        outFormat = "tiffcrle";
                        break;
                    default:
                        break;
                }

                //Convert and dump these files into imaging or PDF
                log($"Received print job: {bfn} for queue {state.queuename}, user {username}.");

                //use ghostpcl to convert to pdf
                System.Diagnostics.Process process = new System.Diagnostics.Process();
                process.StartInfo.FileName = "gpcl6win64.exe";
                process.StartInfo.Arguments = $@"-dNOPAUSE -sDEVICE={outFormat} -sOutputFile={Path.Combine(path, bfn)}{destExt} " +
                    $"-dAutoRotatePages=/All -J {Path.Combine(path, state.dfName)}"; //argument
                //log(process.StartInfo.FileName + " " + process.StartInfo.Arguments);
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                process.StartInfo.CreateNoWindow = true; //not diplay a windows
                process.Start();
                string output = process.StandardOutput.ReadToEnd(); //The output result
                if (process.WaitForExit(60000))
                {
                    //log("gpcl output: " + output);

                    //send email to originating user
                    switch (state.queuename)
                    {
                        case "PDFEMAIL":
                            List<String> att = new List<String>();
                            att.Add($"{Path.Combine(path, bfn)}{destExt}");
                            SendMail($"{username}@{origdomain}", $"pdfsrv@{origdomain}", $"Printer job {jobname}", "", att);
                            log($"Sent email to {username}@{origdomain}.");

                            break;
                        case "IMAGING":
                            //create imaging files
                            //how to get metadata from user?
                            break;
                        default:

                            break;

                    }
                    log("THIS IS A SERVER, PLEASE DO NOT CLOSE THE WINDOW.");
                } else
                {
                    log("Conversion timed out, killing conversion agent.");
                    process.Kill();
                    SendMail($"{username}@{origdomain}", $"pdfsrv@{origdomain}", $"Printer job {jobname} failed", "Conversion timed out, sorry.", null);
                    log("THIS IS A SERVER, PLEASE DO NOT CLOSE THE WINDOW.");
                }
                //delete files
                try
                {
		    //uncomment to clean up
                    if (delfiles)
                    {
                        File.Delete($"{Path.Combine(path, bfn)}{destExt}");
                        File.Delete($"{Path.Combine(path, state.dfName)}");
                    }
                }
                catch { }
                return true;
            }
            return false;
        }

        public static void SendMail(String mailTo, String mailFrom, String subject, String body, List<String> attFiles = null)
        {
            //**TODO: Add authentication
            //SmtpClient constructor also allows a port to be specified
            using (SmtpClient mc = new SmtpClient(smtpserver))
            {
                Attachment att;
                mailFrom = mailFrom.Replace(';', ',');
                MailMessage msg = new MailMessage(mailFrom, mailTo);
                msg.Body = body;
                msg.Subject = subject;

                //there are many ways to provide credentials for smtpclient
                //client.Credentials = CredentialCache.DefaultNetworkCredentials;
                //client.defaultCredentials = true;
                //client.Credentials = new System.Net.NetworkCredential(username, password);
                //client.EnableSsl = true;

                if (attFiles != null)
                {
                    foreach (String attFile in attFiles)
                    {
                        att = new Attachment(attFile);
                        att.Name = Path.GetFileName(attFile);
                        msg.Attachments.Add(att);
                    }
                }

                mc.Send(msg);

                msg.Dispose();
            }
        }


        private static void Send(Socket handler, String data)
        {
            // Convert the string data to byte data using ASCII encoding.  
            byte[] byteData = Encoding.ASCII.GetBytes(data);

            // Begin sending the data to the remote device.  
            handler.BeginSend(byteData, 0, byteData.Length, SocketFlags.None,
                new AsyncCallback(SendCallback), handler);
        }

        private static void SendAck(Socket handler)
        {
            try
            {
                //log("Sending ack");
                // Convert the string data to byte data using ASCII encoding.  
                byte[] ack = new byte[4];
                ack[0] = ack[1] = ack[2] = ack[3] = 0;

                // Begin sending the data to the remote device.  
                handler.BeginSend(ack, 0, 1, SocketFlags.None,
                    new AsyncCallback(SendCallback), handler);
            }
            catch (Exception e)
            {
                log(e.ToString());
            }
        }

        private static void SendCallback(IAsyncResult ar)
        {
            try
            {
                // Retrieve the socket from the state object.  
                Socket handler = (Socket)ar.AsyncState;
                if (!handler.Connected) return;

                // Complete sending the data to the remote device.  
                int bytesSent = handler.EndSend(ar);
                //log($"Sent {bytesSent} bytes to client.");

                //handler.Shutdown(SocketShutdown.Both);
                //handler.Close();

            }
            catch (Exception e)
            {
                log(e.ToString());
            }
        }

    }
}

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
    class lpdaemon
    {
        // Thread signal.  
        public static ManualResetEvent allDone = new ManualResetEvent(false);
        private static List<StateObject> states = new List<StateObject>();

        public static String path = "";
        public static String smtpserver = "";
        public static String origdomain = "";
        public static bool delfiles = true;

        private static Timer timeout;
        private static bool debug = false;

        // Size of receive buffer.  
        public const int BufferSize = 2048;
        // String used to determine a valid hostname
        public const String validHostChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~:/?#[]@!$&'()*+,;=`.";

        // This state object is the only place we store state
        // between received packets. It needs to store both
        // data and flags so that we know what we are doing
        // when we receive new data.
        // Effectively we match state to the origin IP/Port.
        public class StateObject
        {
            // Client  socket.  
            public Socket workSocket = null;
            // Receive buffer.  
            public byte[] buffer = new byte[BufferSize];

            // We receive files one packet at a time,
            // and it may take many packets. So we
            // store what we have here until it is
            // complete. 

            // Received data info  
            // Command file tracking
          
            // The name of the command file we are tracking
            public String cfName = "";
            // The command file data
            public StringBuilder cf = new StringBuilder();
            // This is true while we are receiving a command file
            public bool rcvcf = false;

            // Data file tracking
            // The name of the data file we are tracking
            public String dfName = "";
            // The data file
            public StringBuilder df = new StringBuilder();
            // True when we are receiving a data file
            public bool rcvdf = false;

            // Queue name received from remote. All queues are accepted.
            public String queuename = "";

            // Buffer for partial commands
            public StringBuilder cb = new StringBuilder();

            // Number of bytes we are waiting on for control or data file
            public Int32 bytesremaining = 0;

            // Last time a packet was received for this connection.
            // Used for timeouts
            public DateTime lastPacket;

            // lpr mode means we are in "subcommand" mode
            // There is no way to exit subcommand mode in the protocol
            public bool lprmode = false;
        }

        //Entry point
        public static void StartLPD()
        {
            log("Starting");
            log("THIS IS A SERVER, PLEASE DO NOT CLOSE THE WINDOW.");
            //**TODO: add smtp user and pass

            //Get config options from .config file
            path = removeInvalidChars(ConfigurationManager.AppSettings["spoolpath"], Path.GetInvalidPathChars());
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            smtpserver = removeNonmatchingChars(ConfigurationManager.AppSettings["smtp"], validHostChars);
            origdomain = removeNonmatchingChars(ConfigurationManager.AppSettings["origdomain"], validHostChars);
            delfiles = Convert.ToBoolean(ConfigurationManager.AppSettings["delfiles"]);
            debug = Convert.ToBoolean(ConfigurationManager.AppSettings["debug"]);
            log($"Spoolpath: {path}");
            log($"SMTP: {smtpserver}");
            log($"Domain: {origdomain}");
            log($"Delete Files: {delfiles}");
            log($"Debug: {debug}");

            // Data buffer for incoming data.  
            byte[] bytes = new Byte[BufferSize];

            // Create timer that calls method every 5 seconds
            timeout = new Timer(timeoutProcessor,null,0,5000);

            // Establish the local endpoint for the socket.  
            // The DNS name of the computer  
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

        // Close all sockets
        public static void StopLPD()
        {
            log("Shutting down");
            foreach (StateObject state in states)
            {
                state.workSocket.Shutdown(SocketShutdown.Both);
                state.workSocket.Close();
            }
        }

        private static string removeInvalidChars(String arg, char[] invalidChars)
        {
            String result = arg;
            foreach (char c in invalidChars)
                arg.Replace(Convert.ToString(c), String.Empty);
            return result;
        }

        private static string removeNonmatchingChars(String arg, String validChars)
        {
            String result = "";
            foreach (Char c in arg)
            {
                if (validChars.IndexOf(c) >= 0) result += c;
            }
            return result;
        }

        private static void log(String x)
        {
            // When this is converted to a Windows service,
            // this will write to the event log.
            Console.WriteLine($"{DateTime.Now:HH:mm:ss}: {x}");
        }

        // Every five seconds check for and kill all connections
        // that haven't received a packet for a while.
        private static void timeoutProcessor(object state)
        {
            List<StateObject> deleteList = new List<StateObject>();
            foreach (StateObject s in states)
            {
                if (s.lastPacket < DateTime.Now.AddSeconds(-60))
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

        // This is called when a client connects to our socket
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
            handler.BeginReceive(state.buffer, 0, BufferSize, 0,
                new AsyncCallback(ReadCallback), state);
        }

        // All incoming packets enter here - except for ACKs
        public static void ReadCallback(IAsyncResult ar)
        {
            // We are either receiving a file, in which case
            // bytesremaining will be more than zero, 
            // or a command. We could receive both in the
            // same packet, which is why this isn't simple.
            try
            {
                //if (debug) log("Receive packet");
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
                            // if we have received more data than we are expecting
                            // only process the data we are expecting and save
                            // the rest to be processed as a command
                            if (bytesRead >= state.bytesremaining)
                            {
                                sb.Append(Encoding.ASCII.GetString(
                                    state.buffer, 0, state.bytesremaining));

                                //remove bytes that go to file from remaining bytes
                                byte[] temp = new byte[BufferSize];
                                Array.Copy(state.buffer, state.bytesremaining, temp, 0, bytesRead - state.bytesremaining);
                                Array.Copy(temp, state.buffer, bytesRead - state.bytesremaining);
                                bytesRead -= state.bytesremaining;

                                state.bytesremaining = 0;
                                SendAck(state.workSocket);

                                if (state.rcvcf)
                                {
                                    state.rcvcf = false;
                                    if (doConversion(state)) return;
                                }
                                if (state.rcvdf)
                                {
                                    state.rcvdf = false;
                                    if (doConversion(state)) return;
                                }
                            }
                            else
                            {
                                sb.Append(Encoding.ASCII.GetString(
                                    state.buffer, 0, bytesRead));
                                state.bytesremaining -= bytesRead;
                            }
                        }
                    } // close of if (bytesremaining>0)
                    else if (bytesRead > 0)
                    {
                        // Because bytesremaining is not more than zero, 
                        // this is not file data
                         
                        //All commands end with linefeed. If there's not 
                        //one, store the current buffer and wait for more data
                        state.cb.Append(Encoding.ASCII.GetString(
                            state.buffer, 0, bytesRead));

                        // If there is a LF we assume it is a command
                        if (state.cb.ToString().IndexOf(Convert.ToChar(10)) >= 0)
                            processCommand(state);
                    }

                    // Since our previous packet had >0 bytes, lets wait for more.
                    handler.BeginReceive(state.buffer, 0, BufferSize, 0,
                    new AsyncCallback(ReadCallback), state);
                }
                else 
                {
                    // If we receive a zero byte callback then the 
                    // remote has closed the connection, kill socket.
                    if (debug) log("Received empty packet, closing connection.");
                    state.workSocket.Shutdown(SocketShutdown.Both);
                    state.workSocket.Close();
                    states.Remove(state);
                    
                    // By failing to setup a new BeginReceive / AsyncCallback we will
                    // never receive another callback for this connection,
                    // which effectively ends it.
                }
            }
            catch (Exception ex)
            {
                log(ex.Message);
            }
        }

        // If we aren't receiving a file and we have received
        // data, we assume it is a command and arrive here.
        private static void processCommand(StateObject state)
        {
            //Process the command buffer in the state object, remove lines as they are processed
            String cmdbuf = state.cb.ToString();
            String cmd;
            String filebuff;
            Int32 length;
            String filename;

            // Commands are terminated with a LF, which is decimal 10 character
            while (cmdbuf.Length > 0 && cmdbuf.IndexOf(Convert.ToChar(10)) >= 0)
            {
                // Pull out everything up to the first LF
                cmd = (cmdbuf.Substring(0, cmdbuf.IndexOf(Convert.ToChar(10))));
                if (debug) log($"Process command: {cmd}");

                // Remove the processed command from the command buffer
                state.cb.Remove(0, cmd.Length + 1);
                cmdbuf = state.cb.ToString();

                // We are either in subcommand (LPR) mode or not
                if (!state.lprmode)
                {
                    // Command mode
                    // The first byte is the command
                    // Multibyte to byte encoding is just handled magically
                    switch (Convert.ToByte(cmd[0]))
                    {
                        case 2: //LPR command
                            state.queuename = cmd.Substring(1).ToUpper();
                            state.lprmode = true;
                            log($"Received LPR Command for queue {state.queuename}");
                            SendAck(state.workSocket);
                            break;
                        default: //We don't care about other commands
                            Send(state.workSocket, "OK \n");
                            break;
                    }
                }
                else
                {
                    // We are in subcommand mode
                    length = Convert.ToInt32(cmd.Substring(1, cmd.IndexOf(' ') - 1)) + 1;
                    filename = cmd.Substring(cmd.IndexOf(' ') + 1);
                    switch (Convert.ToByte(cmd[0]))
                    {
                        case 2: //control file
                            if (debug) log("Receiving control file");
                            state.cfName = removeInvalidChars(filename,Path.GetInvalidFileNameChars());
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
                            if (debug) log("Receiving control file");
                            state.dfName = removeInvalidChars(filename, Path.GetInvalidFileNameChars());
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

        // This is called after each file is received. But we only take action when
        // both the command and data files are received completely, and we have 
        // received a queue name as well.
        private static bool doConversion(StateObject state)
        {
            if (debug) log("Call doConversion()");

            if (state.rcvcf == false && state.rcvdf == false && state.cf.Length != 0
                && state.df.Length != 0 && state.queuename != "" && state.cf.Length > 0
                && state.df.Length > 0)
            {
                // We are done with the socket, so let's shut it down
                state.workSocket.Shutdown(SocketShutdown.Both);
                state.workSocket.Close();

                // The file name is actually a tiny part of this, but we use it all anyway
                String bfn = Path.GetFileNameWithoutExtension(state.dfName);
                String username = "";
                String jobname = "";
                if (debug) log($"bfn: {bfn}");

                //remove null characters at end of files
                state.cf.Remove(state.cf.Length-1, 1);
                state.df.Remove(state.df.Length-1, 1);

                // Read the control file into separate lines
                string[] lines = new StringReader(state.cf.ToString()).ReadToEnd().Split(Convert.ToChar(10));
                foreach (String l in lines)
                {
                    if (l.Length > 0)
                    {
                        if (l.Substring(0, 1) == "P") username = l.Substring(1);
                        if (l.Substring(0, 1) == "N") jobname = l.Substring(1);
                    }
                }

                if (debug) log($"User: {username}");
                if (debug) log($"job: {jobname}");

                // Modify file name to includ username
                bfn = removeInvalidChars(username, Path.GetInvalidFileNameChars()) + "-" + bfn;
                
                // If we don't have a username we don't know who to email to
                // We could have a default instead.
                if (username == "")
                {
                    log("Can't determine username, aborting.");
                    return true;
                }

                // We finally write the data file to a disk file from memory
                if (debug) log($"Open streamwriter");
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

                if (debug) log($"Ext: {destExt}");
                if (debug) log($"multiImage: {multiImage}");
                if (debug) log($"Output format: {outFormat}");

                //Convert and dump these files into imaging or PDF
                log($"Received print job: {bfn} for queue {state.queuename}, user {username}.");

                //use ghostpcl to convert to pdf
                System.Diagnostics.Process process = new System.Diagnostics.Process();
                process.StartInfo.FileName = "gpcl6win64.exe";
                process.StartInfo.Arguments = $@"-dNOPAUSE -dBATCH -sDEVICE={outFormat} -sOutputFile={Path.Combine(path, bfn)}{destExt} " +
                    $"-dAutoRotatePages=/All -J {Path.Combine(path, state.dfName)}"; //argument
                if (debug) log(process.StartInfo.FileName + " " + process.StartInfo.Arguments);
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                process.StartInfo.CreateNoWindow = true; //not diplay a windows
                process.Start();
                string output = process.StandardOutput.ReadToEnd(); //The output result
                if (process.WaitForExit(300000)) //five minute timeout
                {
                    if (debug) log("gpcl output: " + output);

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
                    if (delfiles)
                    {
                        if (debug) log("Deleting files");
                        File.Delete($"{Path.Combine(path, bfn)}{destExt}");
                        File.Delete($"{Path.Combine(path, state.dfName)}");
                    }
                }
                catch { }

                // Free up memory
                states.Remove(state);

                return true;
            }
            return false;
        }

        // Send an email
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

        // Send given data
        private static void Send(Socket handler, String data)
        {
            // Convert the string data to byte data using ASCII encoding.  
            byte[] byteData = Encoding.ASCII.GetBytes(data);

            // Begin sending the data to the remote device.  
            handler.BeginSend(byteData, 0, byteData.Length, SocketFlags.None,
                new AsyncCallback(SendCallback), handler);
        }

        // Send an ack
        private static void SendAck(Socket handler)
        {
            try
            {
                if (debug) log("Sending ack");
                // Convert the string data to byte data using ASCII encoding.  
                byte[] ack = new byte[1];
                ack[0] = 0;

                // Begin sending the data to the remote device.  
                // When we receive an ACK the callback executes.
                handler.BeginSend(ack, 0, 1, SocketFlags.None,
                    new AsyncCallback(SendCallback), handler);
            }
            catch (Exception e)
            {
                log(e.ToString());
            }
        }

        // We have received an ACK for data we send.
        // We don't actually do anything with it
        private static void SendCallback(IAsyncResult ar)
        {
            try
            {
                // Retrieve the socket from the state object.  
                Socket handler = (Socket)ar.AsyncState;
                if (!handler.Connected) return;

                // Complete sending the data to the remote device.  
                int bytesSent = handler.EndSend(ar);
                if (debug) log($"Remote host acknowledged {bytesSent} bytes.");
            }
            catch (Exception e)
            {
                log(e.ToString());
            }
        }

    }
}

This is an LPD server written in c# that processes print files from an iSeries
host and translates them into image formats using GhostScript.

iSeries -> PCL -> LPD protocol -> RQS -> GhostPCL -> PDF -> Email

Note: Thiz service accepts any print file by LPD and passes it through a
program.  It could be expanded to cover a wide range of use cases, including
scenarios that don't involve iSeries. 

First you will need to create a remote output queue:

http://www-01.ibm.com/support/docview.wss?uid=nas8N1010090

  crtoutq (Then hit F4)
  Output Queue: pdfsrv
  Library: qusrsys
  (Hit F10 for more)
  Remote system: *INTNETADR
  Remote printer queue: 'PDFEMAIL'
  Connection Type: *IP
  Destination type: *other
  Host print transform: yes
  Manufacturer type and model: *HPII
  Internet Address: '192.168.1.22'
  (Hit Enter to create)
  
  strrmtwtr pfdsrv

You can end with
  endwtr pdfsrv option(*immed)

Edit the config file and add your SMTP server, domain, etc.

This program uses GhostPCL to convert PCL printer files to PDF.
https://www.ghostscript.com/GhostPCL.html

Copy these two ghostPCL files into your program folder before opening the project.
Or just make sure they are in the executable folder when the server is run.

gpcl6dll64.dll
gpcl6win64.exe

You may need to start it up as administrator since it's a low port. 
Make sure to open firewall ports as needed.

Please note: Some spool files do not have SCS data created correctly. You can
view the remote queue job log to see this:

  dspjob job(pdfsrv)

These jobs will never print via this method because they do not have the data
needed to create the output. When you release the job it connects to the
server, checks the queue and then disconnects. On the iSeries host the status
just immediately goes back to HLD. 

But a new output file created for this remote output queue should always work.
Set the default output queue for users to this output queue and there should be
no issues. 

Here is some info:

http://www-01.ibm.com/support/docview.wss?uid=nas8N1019550


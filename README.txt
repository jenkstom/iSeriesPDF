This is an LPD service written in c# that processes print files from an iSeries host
and translates them into image formats using GhostScript.

iSeries -> PCL -> LPD protocol -> RQS -> GhostPCL -> PDF -> Email

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

OpenVSA cold-start measurement
==============================

What this is for
----------------
One number: how long OpenVSA takes, from a standing start, to show its first trace.
REQ-NFR-025 says three seconds. Issue #410 is open because that figure can only be
taken on a machine that has NEVER had OpenVSA installed, and installing it destroys
that property permanently -- so it cannot be taken on a development machine.


What you need
-------------
A Windows 10 or 11 machine, 64-bit, that has never had OpenVSA on it.
Nothing else. No Visual Studio, no .NET SDK, no source code.

A virtual machine is fine, and is the easy way to get a clean one. Say so in
the reply if you used one -- a VM's disk behaves differently from a real one,
and cold start is mostly disk.


What to do
----------
1. Copy this whole folder to the clean machine. Keep it together: the harness
   sits in the harness\ subfolder and the script will not run without it.

2. Run the installer:  OpenVSA-<version>-x64.msi
   Accept the defaults. It may warn that VISA is not installed -- that is
   expected and correct; OpenVSA runs without it.

3. DO NOT OPEN OPENVSA YET. This is the part that matters. The first launch is
   the only cold one there will ever be on this machine, and opening it by hand
   spends it. The script checks and will tell you if it has already happened.

4. Wait about five minutes after the install finishes. The installer asks Windows
   to pre-compile OpenVSA in the background at idle priority, and that is most of
   what the measurement is about. The script records whether it has finished, so
   this is not fatal either way -- but the figure is more useful if it has.

5. Double-click RUN-ME.cmd

   OpenVSA will open and close by itself five times. Please do not touch the
   keyboard or mouse while it does -- the script drives the menus, and a click of
   your own lands in the middle of the measurement.

   It takes about a minute.

6. Send back the file it names at the end:  coldstart-<machine>-<date>.log


What it does and does not do
----------------------------
It writes one log file and stops. It sends nothing anywhere, changes no settings,
and installs nothing of its own. The log contains the machine's model, processor,
memory, storage type, Windows version and the timings -- so read it before sending
if you would rather not share any of that.

If something goes wrong the log still gets written, and it is still worth sending:
a failure to measure looks quite different from a slow start, and the log says
which happened.

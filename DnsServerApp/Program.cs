/*
Technitium DNS Server
Copyright (C) 2026  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using DnsServerCore;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace DnsServerApp
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Modified by Tekmyster Industries, LLC — remains GPL-3.0.
            if (args.Length > 0 && args[0] == "--dhcp-recover-unused")
            {
                try
                {
                    if (args.Length != 8) throw new ArgumentException("Usage: --dhcp-recover-unused original-store owner scope receiver1,receiver2 recovery-id expected-sequence output-json. Stop the service and externally fence this owner first.");
                    string storePath = System.IO.Path.GetFullPath(args[1]), outputPath = System.IO.Path.GetFullPath(args[7]);
                    if (string.Equals(storePath, outputPath, StringComparison.OrdinalIgnoreCase) || outputPath.StartsWith(storePath + ".", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Export recovery evidence to a separate file, outside the authoritative journal and its barriers.");
                    await using var store = new DnsServerCore.Dhcp.DistributedAllocation(args[1]) { NodeName = () => args[2] };
                    if (store.PersistedNode != args[2]) throw new InvalidOperationException("Recovery must use the original owner's watermark-bound journal.");
                    store.RecoverUnusedCapacity(args[3], args[4].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), args[5], long.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture));
                    System.IO.File.WriteAllBytes(args[7], store.Export());
                    Console.WriteLine("Unused dynamic capacity durably surrendered. Keep the external fence until every receiver persists the grants; never restore an older owner journal.");
                }
                catch (Exception exception) { Console.Error.WriteLine(exception.Message); Environment.ExitCode = 1; }
                return;
            }
            bool throwIfBindFails = false;
            bool isPortableApp = false; //true if logs folder should be inside the config folder else will use OS specific log folder path
            string? configFolder = null;

            foreach (string arg in args)
            {
                switch (arg)
                {
                    case "--icu-test":
                        _ = System.Globalization.CultureInfo.CurrentCulture;
                        return;

                    case "--stop-if-bind-fails":
                        throwIfBindFails = true;
                        break;

                    case "--portable-app":
                        isPortableApp = true;
                        break;

                    default:
                        configFolder = arg;
                        break;
                }
            }

            ManualResetEvent waitHandle = new ManualResetEvent(false);
            ManualResetEvent exitHandle = new ManualResetEvent(false);
            DnsWebService? service = null;
            PosixSignalRegistration? psr = null;

            try
            {
                if (!isPortableApp)
                    isPortableApp = configFolder is null; //no config folder specified so run as portable app

                // teQtanium releases must not redirect users to an upstream installer.
                service = new DnsWebService(isPortableApp, configFolder);
                await service.StartAsync(throwIfBindFails);

                Console.CancelKeyPress += delegate (object? sender, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;
                    waitHandle.Set();
                };

                AppDomain.CurrentDomain.ProcessExit += delegate (object? sender, EventArgs e)
                {
                    waitHandle.Set();
                    exitHandle.WaitOne();
                };

                if (Environment.OSVersion.Platform == PlatformID.Unix)
                {
                    psr = PosixSignalRegistration.Create(PosixSignal.SIGTERM, delegate (PosixSignalContext context)
                    {
                        waitHandle.Set();
                        exitHandle.WaitOne();
                    });
                }

                string serviceUrl;

                if (service.IsWebServiceTlsEnabled)
                    serviceUrl = $"https://{Environment.MachineName.ToLowerInvariant()}:{service.WebServiceTlsPort}";
                else
                    serviceUrl = $"http://{Environment.MachineName.ToLowerInvariant()}:{service.WebServiceHttpPort}";

                Console.WriteLine($"teQtanium DDI Server {service.ProductVersion} was started successfully.\r\nStarted on: {service.UpTimeStamp.ToLocalTime()} Local Time\r\nUsing config folder: {service.ConfigFolder}\r\n\r\nNote: Open {serviceUrl} in web browser to access web console.\r\n\r\nPress [CTRL + C] to stop...");

                waitHandle.WaitOne();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                Environment.ExitCode = 1; // Allow service supervisors to detect and retry failed startup.
            }
            finally
            {
                Console.WriteLine("\r\nTectanium DDI Server is stopping...");

                service?.Dispose();
                psr?.Dispose();

                Console.WriteLine("teQtanium DDI Server was stopped successfully.");
                exitHandle.Set();
            }
        }
    }
}

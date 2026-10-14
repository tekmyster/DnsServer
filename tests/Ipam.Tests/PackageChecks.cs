using DnsServerCore;
using DnsServerCore.Dns;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

static class PackageChecks
{
    static int Port() { var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port; }
    static async Task Prepare(string folder, int port)
    {
        Directory.CreateDirectory(folder);
        string repository=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        await using var log=new LogManager(true,folder);
        using var dns=new DnsServer(folder,Path.Combine(repository,"DnsServerCore/dohwww"),log);
        dns.LoadConfigFile();
        dns.LocalEndPoints=[new IPEndPoint(IPAddress.Loopback,port)];
        dns.EnableCheckForUpdate=false;
        dns.SaveConfigFile(true);
    }
    public static async Task Run(string[] args)
    {
        if (args[0]=="--prepare-package") { await Prepare(Path.GetFullPath(args[1]),int.Parse(args[2]));return; }
        string executable=Path.GetFullPath(args[1]);
        string folder=Path.Combine(Path.GetTempPath(),"teqnatium-package-"+Guid.NewGuid().ToString("N"));
        int dnsPort=Port(),webPort=Port();await Prepare(folder,dnsPort);
        string passwordFile=Path.Combine(folder,"credential.txt");File.WriteAllText(passwordFile,Guid.NewGuid().ToString("N")+"Aa!9");
        ProcessStartInfo start=new(executable.EndsWith(".dll")?"dotnet":executable) { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(executable)! };
        if (executable.EndsWith(".dll")) start.ArgumentList.Add(executable);
        start.ArgumentList.Add("--portable-app");start.ArgumentList.Add("--stop-if-bind-fails");start.ArgumentList.Add(folder);
        start.Environment["DNS_SERVER_ADMIN_PASSWORD_FILE"]=passwordFile;
        start.Environment["DNS_SERVER_WEB_SERVICE_LOCAL_ADDRESSES"]="127.0.0.1";
        start.Environment["DNS_SERVER_WEB_SERVICE_HTTP_PORT"]=webPort.ToString();
        start.Environment["DNS_SERVER_LOG_FOLDER_PATH"]=Path.Combine(folder,"logs");
        using var process=Process.Start(start)!;
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        try
        {
            using var http=new HttpClient(new HttpClientHandler { UseProxy=false }) { Timeout=TimeSpan.FromSeconds(1) };
            bool ready=false;
            for(int i=0;i<60;i++)
            {
                if(process.HasExited) throw new Exception("Packaged application exited: "+await error+await output);
                try { var response=await http.GetAsync($"http://127.0.0.1:{webPort}/"); if(response.IsSuccessStatusCode) { ready=true;break; } } catch(HttpRequestException) { } catch(TaskCanceledException) { }
                await Task.Delay(500);
            }
            if(!ready) throw new Exception("Packaged application failed to start.");
            var check=new ProcessStartInfo("python") { UseShellExecute=false };
            string repository=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
            foreach(string argument in new[]{Path.Combine(repository,"packaging/check-service.py"),$"http://127.0.0.1:{webPort}",passwordFile,"--dns-port",dnsPort.ToString()}) check.ArgumentList.Add(argument);
            using var validation=Process.Start(check)!;await validation.WaitForExitAsync();
            if(validation.ExitCode!=0) throw new Exception("Packaged service validation failed.");
        }
        finally
        {
            if(!process.HasExited) { process.Kill(true);await process.WaitForExitAsync(); }
            if(Path.GetDirectoryName(folder)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)) throw new Exception("Unexpected fixture directory");
            Directory.Delete(folder,true);
        }
    }
}

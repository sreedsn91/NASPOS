using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.ServiceProcess;
using Microsoft.Win32;

namespace KshethraSetup;

internal sealed class SqlInstance
{
    public string Name { get; set; } = "";       // MSSQLSERVER, SQLEXPRESS, ...
    public string Server { get; set; } = "";     // what goes into a connection string
    public string Service { get; set; } = "";
    public string Version { get; set; } = "";
    public string Edition { get; set; } = "";
    public string Status { get; set; } = "";
}

internal sealed class SqlCheck
{
    public bool Connected { get; set; }
    public bool IsSysAdmin { get; set; }
    public bool WindowsAuthOnly { get; set; }
    public string Version { get; set; } = "";
    public string Error { get; set; } = "";
}

/// <summary>Finds SQL Server instances installed on this PC (same registry key install.ps1 reads) and tests admin connections.</summary>
internal static class SqlDiscovery
{
    public static List<SqlInstance> LocalInstances()
    {
        var list = new List<SqlInstance>();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var names = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
        if (names == null) return list;
        foreach (string name in names.GetValueNames())
        {
            string id = names.GetValue(name) as string ?? "";
            using var setup = hklm.OpenSubKey($@"SOFTWARE\Microsoft\Microsoft SQL Server\{id}\Setup");
            bool isDefault = name.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase);
            var inst = new SqlInstance
            {
                Name = name,
                Server = isDefault ? "localhost" : @"localhost\" + name,
                Service = isDefault ? "MSSQLSERVER" : "MSSQL$" + name,
                Version = FriendlyVersion(setup?.GetValue("Version") as string ?? ""),
                Edition = setup?.GetValue("Edition") as string ?? "",
            };
            inst.Status = ServiceStatus(inst.Service);
            list.Add(inst);
        }
        return list;
    }

    public static string ServiceStatus(string service)
    {
        try { using var sc = new ServiceController(service); return sc.Status.ToString(); }
        catch { return "Unknown"; }
    }

    /// <summary>Starts a stopped local instance so it can be tested (setup runs elevated).</summary>
    public static void EnsureRunning(string service)
    {
        using var sc = new ServiceController(service);
        if (sc.Status == ServiceControllerStatus.Running) return;
        if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
    }

    public static SqlCheck Test(string server, string? user, string? password)
    {
        var csb = new SqlConnectionStringBuilder { DataSource = server, InitialCatalog = "master", ConnectTimeout = 15 };
        csb["TrustServerCertificate"] = true;
        if (string.IsNullOrEmpty(user)) csb.IntegratedSecurity = true;
        else { csb.UserID = user; csb.Password = password ?? ""; }
        try
        {
            using var cn = new SqlConnection(csb.ConnectionString);
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT IS_SRVROLEMEMBER('sysadmin'), CONVERT(int, SERVERPROPERTY('IsIntegratedSecurityOnly')), CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'))";
            using var r = cmd.ExecuteReader();
            r.Read();
            return new SqlCheck
            {
                Connected = true,
                IsSysAdmin = !r.IsDBNull(0) && r.GetInt32(0) == 1,
                WindowsAuthOnly = !r.IsDBNull(1) && r.GetInt32(1) == 1,
                Version = FriendlyVersion(r.IsDBNull(2) ? "" : r.GetString(2)),
            };
        }
        catch (Exception ex) { return new SqlCheck { Error = ex.Message }; }
    }

    private static string FriendlyVersion(string v)
    {
        string major = v.Split('.')[0];
        string name = major switch
        {
            "17" => "SQL Server 2025", "16" => "SQL Server 2022", "15" => "SQL Server 2019", "14" => "SQL Server 2017",
            "13" => "SQL Server 2016", "12" => "SQL Server 2014", "11" => "SQL Server 2012", _ => "",
        };
        return name.Length == 0 ? v : $"{name} ({v})";
    }
}

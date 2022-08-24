﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

using Newtonsoft.Json;
using RobloxDeployHistory;
using Konscious.Security.Cryptography;

namespace RobloxStudioModManager
{
    public class StudioBootstrapper
    {
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool FlashWindow(IntPtr hWnd, bool bInvert);

        private static string AppSettings_XML;
        private static string OAuth2Config_JSON;

        private const string RepoBranch = "main";
        private const string RepoOwner = "MaximumADHD";
        private const string RepoName = "Roblox-Studio-Mod-Manager";

        private const string UserAgent = "RobloxStudioModManager";
        public const string StartEvent = "RobloxStudioModManagerStart";

        public event MessageEventHandler EchoFeed;
        public event MessageEventHandler StatusChanged;

        public event ChangeEventHandler<int> ProgressChanged;
        public event ChangeEventHandler<ProgressBarStyle> ProgressBarStyleChanged;

        private readonly IBootstrapperState mainState;
        private readonly VersionManifest versionRegistry;
        private readonly Dictionary<string, string> fileRegistry;
        private readonly Dictionary<string, PackageState> pkgRegistry;

        private Dictionary<string, string[]> bySignature;
        private Dictionary<string, string> newManifestEntries;
        private HashSet<string> writtenFiles;
        private FileManifest fileManifest;
        private string buildVersion;
        private string status;

        private int _progress = -1;
        private int _maxProgress = -1;
        private ProgressBarStyle _progressBarStyle;
        private readonly object ProgressLock = new object();

        public static readonly List<string> BadManifests = new List<string>();
        public static readonly Dictionary<string, string> KnownRoots = new Dictionary<string, string>();
        
        public int Progress
        {
            get => _progress;

            private set
            {
                if (_progress == value)
                    return;

                var change = new ChangeEventArgs<int>(value, "Progress");
                ProgressChanged?.Invoke(this, change);

                _progress = value;
            }
        }

        public int MaxProgress
        {
            get => _maxProgress;

            private set
            {
                if (_maxProgress == value)
                    return;

                if (value < Progress)
                    return;

                var change = new ChangeEventArgs<int>(value, "MaxProgress");
                ProgressChanged?.Invoke(this, change);

                _maxProgress = value;
            }
        }

        public ProgressBarStyle ProgressBarStyle
        {
            get => _progressBarStyle;

            private set
            {
                if (_progressBarStyle == value)
                    return;

                var change = new ChangeEventArgs<ProgressBarStyle>(value);
                ProgressBarStyleChanged?.Invoke(this, change);

                _progressBarStyle = value;
            }
        }

        public Channel Channel { get; set; } = "zLive";
        public string OverrideStudioDirectory { get; set; } = "";

        public bool CanShutdownStudio { get; set; } = true;
        public bool CanForceStudioShutdown { get; set; } = false;

        public bool ForceInstall { get; set; } = false;
        public bool SetStartEvent { get; set; } = false;
        public bool GenerateMetadata { get; set; } = false;
        public bool RemapExtraContent { get; set; } = false;
        public bool ApplyModManagerPatches { get; set; } = false;

        public StudioBootstrapper(IBootstrapperState state = null)
        {
            if (state == null)
                mainState = Program.State;
            else
                mainState = state;

            versionRegistry = mainState.VersionData;
            pkgRegistry = mainState.PackageManifest;
            fileRegistry = mainState.FileManifest;
        }

        private void echo(string message)
        {
            var args = new MessageEventArgs(message);
            EchoFeed(this, args);
        }

        private void setStatus(string newStatus)
        {
            if (status != newStatus)
            {
                var args = new MessageEventArgs(newStatus);
                StatusChanged(this, args);
                status = newStatus;
            }
        }

        private static string getDirectory(params string[] paths)
        {
            string basePath = Path.Combine(paths);

            if (!Directory.Exists(basePath))
                Directory.CreateDirectory(basePath);

            return basePath;
        }

        private static string computeSignature(Stream source)
        {
            using (var blake2b = new HMACBlake2B(16 * 8))
            {
                byte[] hash = blake2b.ComputeHash(source);

                string result = BitConverter
                    .ToString(hash)
                    .Replace("-", "")
                    .ToLower(Program.Format);

                return result;
            }
        }

        private static string computeSignature(ZipArchiveEntry entry)
        {
            string signature;

            using (var stream = entry.Open())
                signature = computeSignature(stream);

            return signature;
        }
        
        private void appendNewManifestEntry(string key, string value)
        {
            if (newManifestEntries == null)
                newManifestEntries = new Dictionary<string, string>();

            newManifestEntries[key] = value;
        }

        public static string GetStudioDirectory()
        {
            string localAppData = Environment.GetEnvironmentVariable("LocalAppData");
            return getDirectory(localAppData, "Roblox Studio");
        }

        public static string GetStudioPath()
        {
            string studioDir = GetStudioDirectory();
            return Path.Combine(studioDir, "RobloxStudioBeta.exe");
        }

        public string GetLocalStudioDirectory()
        {
            if (!string.IsNullOrEmpty(OverrideStudioDirectory))
                return OverrideStudioDirectory;

            return GetStudioDirectory();
        }

        public string GetLocalStudioPath()
        {
            string studioDir = GetLocalStudioDirectory();
            return Path.Combine(studioDir, "RobloxStudioBeta.exe");
        }

        private static void tryToKillProcess(Process process)
        {
            try
            {
                process.Kill();
            }
            catch (Win32Exception)
            {
                Console.WriteLine($"Cannot terminate process {process.Id}!");
            }
        }

        public static string GetStudioBinaryType()
        {
            string binaryType = "WindowsStudio";

            if (Environment.Is64BitOperatingSystem)
                binaryType += "64";

            return binaryType;
        }

        public static List<Process> GetRunningStudioProcesses()
        {
            var studioProcs = new List<Process>();

            foreach (Process process in Process.GetProcessesByName("RobloxStudioBeta"))
            {
                Action<Process> action;

                if (process.MainWindowHandle != IntPtr.Zero)
                    action = studioProcs.Add;
                else
                    action = tryToKillProcess;

                action?.Invoke(process);
            }

            return studioProcs;
        }

        private void deleteUnusedFiles()
        {
            string studioDir = GetLocalStudioDirectory();
            setStatus("Deleting unused files...");

            if (newManifestEntries != null)
            {
                foreach (var pair in newManifestEntries)
                {
                    string key = pair.Key,
                           value = pair.Value;

                    if (key == null || value == null)
                        continue;

                    if (fileManifest.ContainsKey(key))
                        continue;

                    fileRegistry[key] = value;
                    fileManifest[key] = value;
                }
            }

            var fileNames = fileRegistry.Keys.ToList();

            foreach (string fileName in fileNames)
            {
                string filePath = Path.Combine(studioDir, fileName);
                string lookupKey = fileName;

                foreach (string pkgName in BadManifests)
                    if (fileName.StartsWith(pkgName, Program.StringFormat))
                        lookupKey = fileName.Substring(pkgName.Length + 1);

                if (!fileManifest.ContainsKey(lookupKey))
                {
                    if (File.Exists(filePath))
                    {
                        var info = new FileInfo(filePath);
                        string oldHash = fileRegistry[fileName];
                        
                        if (oldHash?.Length > 32)
                            if (info.Extension == ".dll" || info.Name == "qmldir" || filePath.Contains("api-docs"))
                                continue;

                        echo($"Deleting unused file {fileName}");

                        try
                        {
                            fileRegistry.Remove(fileName);
                            File.Delete(filePath);
                        }
                        catch (IOException)
                        {
                            Console.WriteLine($"IOException thrown while trying to delete {fileName}");
                        }
                        catch (UnauthorizedAccessException)
                        {
                            Console.WriteLine($"UnauthorizedAccessException thrown while trying to delete {fileName}");
                        }
                    }
                    else if (fileRegistry.ContainsKey(fileName))
                    {
                        fileRegistry.Remove(fileName);
                    }
                }
            }
        }

        public static async Task<ClientVersionInfo> GetTargetVersionInfo(Channel channel, string targetVersion, VersionManifest versionRegistry = null)
        {
            if (versionRegistry == null)
                versionRegistry = Program.State.VersionData;

            var logData = await StudioDeployLogs
                .Get(channel)
                .ConfigureAwait(false);

            HashSet<DeployLog> targets;

            if (Environment.Is64BitOperatingSystem)
                targets = logData.CurrentLogs_x64;
            else
                targets = logData.CurrentLogs_x86;

            DeployLog target = targets
                .Where(log => log.VersionId == targetVersion)
                .FirstOrDefault();

            if (target == null)
            {
                var result = GetCurrentVersionInfo(channel, versionRegistry);
                return await result.ConfigureAwait(false);
            }

            return new ClientVersionInfo(target);
        }

        public static async Task<ClientVersionInfo> GetCurrentVersionInfo(Channel channel, VersionManifest versionRegistry = null, string targetVersion = "")
        {
            if (versionRegistry == null)
                versionRegistry = Program.State.VersionData;

            if (!string.IsNullOrEmpty(targetVersion))
            {
                var result = GetTargetVersionInfo(channel, targetVersion, versionRegistry);
                return await result.ConfigureAwait(false);
            }

            bool is64Bit = Environment.Is64BitOperatingSystem;
            ClientVersionInfo info;

            var logData = await StudioDeployLogs
                .Get(channel)
                .ConfigureAwait(false);

            DeployLog build_x86 = logData.CurrentLogs_x86.Last();
            DeployLog build_x64 = logData.CurrentLogs_x64.Last();

            if (is64Bit)
                info = new ClientVersionInfo(build_x64);
            else
                info = new ClientVersionInfo(build_x86);

            versionRegistry.LatestGuid_x64 = build_x64.VersionGuid;
            versionRegistry.LatestGuid_x86 = build_x86.VersionGuid;
            
            return info;
        }

        private string fixFilePath(string pkgName, string filePath)
        {
            string pkgDir = pkgName.Replace(".zip", "");

            if (BadManifests.Contains(pkgDir))
                if (!filePath.StartsWith(pkgDir, Program.StringFormat))
                    filePath = pkgDir + '\\' + filePath;

            if (RemapExtraContent)
                filePath = filePath.Replace("ExtraContent", "content");

            return filePath.Replace('/', '\\');
        }

        private bool shouldFetchPackage(Package package)
        {
            string pkgName = package.Name;

            if (!pkgRegistry.TryGetValue(pkgName, out var pkgInfo))
            {
                pkgInfo = new PackageState();
                pkgRegistry[pkgName] = pkgInfo;
            }

            string oldSig = pkgInfo.Signature;
            string newSig = package.Signature;

            if (oldSig == newSig && !ForceInstall)
            {
                echo($"Package '{pkgName}' hasn't changed between builds, skipping.");
                return false;
            }

            return true;
        }

        private async Task<bool> packageExists(Package package)
        {
            echo($"Verifying availability of: {package.Name}");

            string pkgName = package.Name;
            var zipFileUrl = new Uri($"https://setup.rbxcdn.com/channel/{Channel}/{buildVersion}-{pkgName}");

            var request = WebRequest.Create(zipFileUrl) as HttpWebRequest;
            request.Headers.Set("UserAgent", UserAgent);
            request.Method = "HEAD";

            var response = await request
                .GetResponseAsync() 
                .ConfigureAwait(false)
                as HttpWebResponse;

            var statusCode = response.StatusCode;
            response.Close();

            return (statusCode == HttpStatusCode.OK);
        }

        private async Task<byte[]> installPackage(Package package)
        {
            byte[] result = null;
            string pkgName = package.Name;
            string zipFileUrl = $"https://setup.rbxcdn.com/channel/{Channel}/{buildVersion}-{pkgName}";

            using (var localHttp = new WebClient())
            {
                int lastProgress = 0;
                bool setMaxProgress = false;
                localHttp.Headers.Set("UserAgent", UserAgent);

                localHttp.DownloadProgressChanged += new DownloadProgressChangedEventHandler((sender, e) =>
                {
                    lock (ProgressLock)
                    {
                        if (!setMaxProgress)
                        {
                            MaxProgress += (int)e.TotalBytesToReceive;
                            setMaxProgress = true;
                        }

                        int progress = (int)e.BytesReceived;

                        if (progress > lastProgress)
                        {
                            int diff = progress - lastProgress;
                            lastProgress = progress;
                            Progress += diff;
                        }
                    }
                });

                echo($"Installing package {zipFileUrl}");
                
                var getFile = localHttp.DownloadDataTaskAsync(zipFileUrl);
                byte[] fileContents = await getFile.ConfigureAwait(false);
                    
                // If the size of the file we downloaded does not match the packed 
                // size specified in the manifest, then this file isn't valid.

                if (fileContents.Length != package.PackedSize)
                    throw new InvalidDataException($"{pkgName} expected packed size: {package.PackedSize} but got: {fileContents.Length}");

                result = fileContents;
            }

            return result;
        }

        private void extractPackage(Package package)
        {
            var data = package.Data;
            string pkgName = package.Name;
            string studioDir = GetLocalStudioDirectory();

            if (!pkgRegistry.TryGetValue(pkgName, out var pkgInfo))
            {
                pkgInfo = new PackageState();
                pkgRegistry[pkgName] = pkgInfo;
            }
            
            string downloads = getDirectory(studioDir, "downloads");
            string zipExtractPath = Path.Combine(downloads, pkgName);

            File.WriteAllBytes(zipExtractPath, data);

            using (var archive = ZipFile.OpenRead(zipExtractPath))
            {
                var deferred = new Dictionary<ZipArchiveEntry, string>();

                int numFiles = archive.Entries
                    .Select(entry => entry.FullName)
                    .Where(name => !name.EndsWith("/", Program.StringFormat))
                    .Count();

                string localRootDir = null;

                if (KnownRoots.ContainsKey(pkgName))
                    localRootDir = KnownRoots[pkgName];

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    bool skip = false;

                    if (entry.Length == 0)
                        skip = true;

                    if (entry.Name.EndsWith(".robloxrc", Program.StringFormat))
                        skip = true;

                    if (entry.Name.EndsWith(".luarc", Program.StringFormat))
                        skip = true;

                    if (skip)
                        continue;
                    
                    string newFileSig = null;
                    string entryPath = entry.FullName.Replace('/', '\\');

                    // If we have figured out what our root directory is, try to resolve
                    // what the signature of this file is.

                    if (localRootDir != null)
                    {
                        // Append local directory to our path.
                        var manifestKey = localRootDir + entryPath;
                        bool hasEntryPath = fileManifest.ContainsKey(manifestKey);
                        
                        if (!hasEntryPath)
                        {
                            // If we can't find this file in the signature lookup table,
                            // try falling back to the entryPath.

                            manifestKey = entryPath;
                            hasEntryPath = fileManifest.ContainsKey(manifestKey);
                        }

                        // If we can find this file path in the file manifest, then we will
                        // use its pre-computed signature to check if the file has changed.

                        newFileSig = hasEntryPath ? fileManifest[manifestKey] : null;
                    }
                    else
                    {
                        var query = fileManifest.Where(pair => pair.Key.EndsWith(entryPath, Program.StringFormat));
                        newFileSig = query.Any() ? query.First().Value : null;
                    }

                    // If we couldn't pre-determine the file signature from the manifest,
                    // then we have to compute it manually. This is slower.

                    if (newFileSig == null)
                        newFileSig = computeSignature(entry);

                    // Now check what files this signature corresponds with.
                   
                    if (bySignature.TryGetValue(newFileSig, out var files))
                    {
                        foreach (string file in files)
                        {
                            // Write the file from this signature.
                            WritePackageFile(studioDir, pkgName, file, newFileSig, entry);

                            if (localRootDir == null)
                            {
                                string filePath = fixFilePath(pkgName, file);

                                if (filePath != file)
                                    appendNewManifestEntry(filePath, newFileSig);
                                
                                if (filePath.EndsWith(entryPath, Program.StringFormat))
                                {
                                    // We can infer what the root extraction  
                                    // directory is for the files in this package!                                 
                                    localRootDir = filePath.Replace(entryPath, "");
                                }
                            }
                        }
                    }
                    else
                    {
                        string file = entry.FullName;

                        if (string.IsNullOrEmpty(localRootDir))
                        {
                            // Check back on this file after we extract the regular files,
                            // so we can make sure this is extracted to the correct directory.
                            deferred.Add(entry, newFileSig);
                        }
                        else
                        {
                            // Append the local root directory.
                            file = localRootDir + file;
                            WritePackageFile(studioDir, pkgName, file, newFileSig, entry);
                        }
                    }
                }

                // Process any files that we deferred from writing immediately.
                foreach (ZipArchiveEntry entry in deferred.Keys)
                {
                    string file = entry.FullName;
                    string newFileSig = deferred[entry];

                    if (localRootDir != null)
                        file = localRootDir + file;

                    if (!fileManifest.ContainsKey(file))
                        appendNewManifestEntry(file, newFileSig);
                    
                    WritePackageFile(studioDir, pkgName, file, newFileSig, entry);
                }

                // Update the signature in the package registry so we can check
                // if this zip file needs to be updated in future versions.

                pkgInfo.Signature = package.Signature;
                pkgInfo.NumFiles = numFiles;
            }
        }

        private void WritePackageFile(string studioDir, string pkgName, string file, string newFileSig, ZipArchiveEntry entry)
        {
            string filePath = fixFilePath(pkgName, file);

            if (writtenFiles.Contains(filePath))
                return;

            if (filePath != file)
                appendNewManifestEntry(filePath, newFileSig);

            if (!ForceInstall)
            {
                if (!fileRegistry.TryGetValue(filePath, out string oldFileSig))
                    oldFileSig = "";

                if (oldFileSig == newFileSig)
                {
                    return;
                }
            }

            string extractPath = Path.Combine(studioDir, filePath);
            string extractDir = Path.GetDirectoryName(extractPath);

            getDirectory(extractDir);

            try
            {
                if (File.Exists(extractPath))
                    File.Delete(extractPath);

                echo($"Writing {filePath}...");
                entry.ExtractToFile(extractPath);

                fileRegistry[filePath] = newFileSig;
            }
            catch (UnauthorizedAccessException)
            {
                echo($"FILE WRITE FAILED: {filePath} (This build may not run as expected!)");
            }

            writtenFiles.Add(filePath);
        }

        private async Task<bool> shutdownStudioProcesses()
        {
            bool cancelled = false;
            bool safeToContinue = false;

            while (!safeToContinue)
            {
                List<Process> running = GetRunningStudioProcesses();

                if (running.Count > 0)
                {
                    setStatus("Shutting down Roblox Studio...");

                    foreach (Process p in running)
                    {
                        if (CanForceStudioShutdown)
                        {
                            tryToKillProcess(p);
                            continue;
                        }

                        SetForegroundWindow(p.MainWindowHandle);
                        FlashWindow(p.MainWindowHandle, true);

                        var delay = Task.Delay(50);
                        p.CloseMainWindow();

                        await delay.ConfigureAwait(true);
                    }

                    List<Process> runningNow = null;

                    const int retries = 10;
                    const int granularity = 300;

                    Progress = 0;
                    MaxProgress = retries * granularity;
                    ProgressBarStyle = ProgressBarStyle.Continuous;

                    for (int i = 0; i < retries; i++)
                    {
                        runningNow = GetRunningStudioProcesses();

                        if (runningNow.Count == 0)
                        {
                            safeToContinue = true;
                            break;
                        }
                        else
                        {
                            var delay = Task.Delay(1000);
                            Progress += granularity;

                            await delay.ConfigureAwait(true);
                        }
                    }

                    if (runningNow.Count > 0 && !safeToContinue)
                    {
                        DialogResult result = DialogResult.OK;

                        if (mainState == Program.State)
                        {
                            result = MessageBox.Show
                            (
                                "All Roblox Studio processes need to be closed in order to update Roblox Studio!\n" +
                                "Press Ok once you've saved your work, or\n" +
                                "Press Cancel to skip this update temporarily.",

                                "Notice",
                                MessageBoxButtons.OKCancel,
                                MessageBoxIcon.Warning
                            );
                        }

                        if (result == DialogResult.Cancel)
                        {
                            safeToContinue = true;
                            cancelled = true;
                        }
                    }
                }
                else
                {
                    safeToContinue = true;
                }
            }

            return !cancelled;
        }

        public async Task<bool> Bootstrap(string targetVersion = "")
        {
            setStatus("Checking for updates");
            echo("Checking build installation...");

            string baseConfigUrl = $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/{RepoBranch}/Config/";
            string currentVersion = versionRegistry.VersionGuid;
            string currentChannel;

            if (mainState == Program.State)
                currentChannel = mainState.Channel;
            else
                currentChannel = Channel;

            var getVersionInfo = GetCurrentVersionInfo(currentChannel, versionRegistry, targetVersion);
            ClientVersionInfo versionInfo = await getVersionInfo.ConfigureAwait(true);

            string studioDir = GetLocalStudioDirectory();
            buildVersion = versionInfo.VersionGuid;
            
            if (currentVersion != buildVersion || ForceInstall)
            {
                echo("This build needs to be installed!");
                bool studioClosed = true;

                if (CanShutdownStudio)
                {
                    var closeStudio = shutdownStudioProcesses();
                    studioClosed = await closeStudio.ConfigureAwait(true);
                }
                
                if (studioClosed)
                {
                    string binaryType = GetStudioBinaryType();
                    string versionId = versionInfo.Version;

                    var installVersion = versionId
                        .Split('.')
                        .Select(int.Parse)
                        .Skip(1)
                        .First();

                    KnownRoots.Clear();
                    BadManifests.Clear();

                    using (var http = new WebClient())
                    {
                        var json = await http
                            .DownloadStringTaskAsync(baseConfigUrl + "KnownRoots.json")
                            .ConfigureAwait(false);

                        var knownRoots = JsonConvert.DeserializeObject<Dictionary<string, KnownRoot>>(json);

                        foreach (var pair in knownRoots)
                        {
                            string name = pair.Key;
                            var knownRoot = pair.Value;

                            if (installVersion < knownRoot.MinVersion)
                                continue;

                            string file = name + ".zip";
                            string extractPath = knownRoot.ExtractTo;

                            if (!string.IsNullOrEmpty(extractPath))
                                extractPath += '/';

                            if (knownRoot.BadManifest)
                                BadManifests.Add(name);

                            KnownRoots.Add(file, extractPath);
                        }
                    }

                    setStatus($"Installing Version {versionId} of Roblox Studio...");
                    echo("Grabbing package manifest...");

                    var pkgManifest = await PackageManifest
                        .Get(versionInfo)
                        .ConfigureAwait(true);

                    echo("Grabbing file manifest...");

                    fileManifest = await FileManifest
                        .Get(versionInfo, RemapExtraContent)
                        .ConfigureAwait(true);

                    var taskQueue = new List<Task>();
                    writtenFiles = new HashSet<string>();

                    bySignature =
                        (from pair in fileManifest
                         let key = pair.Key
                         let value = pair.Value
                         group key by value)
                        .ToDictionary(group => group.Key, group => group.ToArray());

                    Progress = 0;
                    MaxProgress = 0;
                    ProgressBarStyle = ProgressBarStyle.Continuous;

                    foreach (Package package in pkgManifest)
                    {
                        package.ShouldInstall = shouldFetchPackage(package);

                        if (!package.ShouldInstall)
                        {
                            package.Exists = true;
                            continue;
                        }

                        Task verify = Task.Run(async () =>
                        {
                            var doesExist = packageExists(package);
                            package.Exists = await doesExist.ConfigureAwait(false);
                        });

                        taskQueue.Add(verify);
                    }

                    await Task
                       .WhenAll(taskQueue)
                       .ConfigureAwait(true);

                    taskQueue.Clear();

                    foreach (Package package in pkgManifest)
                    {
                        if (!package.Exists)
                        {
                            setStatus("Installation Failed!");
                            echo($"ERROR WHILE INSTALLING: Package {package.Name} could not be fetched! Skipping installation for now.");

                            Progress = 1;
                            MaxProgress = 1;
                            ProgressBarStyle = ProgressBarStyle.Marquee;

                            var timeout = Task.Delay(2000);
                            await timeout.ConfigureAwait(false);

                            return false;
                        }

                        if (!package.ShouldInstall)
                            continue;

                        var installer = Task.Run(async () =>
                        {
                            var install = installPackage(package);
                            package.Data = await install.ConfigureAwait(false);

                            extractPackage(package);
                            return true;
                        });

                        taskQueue.Add(installer);
                    }

                    await Task
                        .WhenAll(taskQueue)
                        .ConfigureAwait(true);

                    foreach (var task in taskQueue)
                    {
                        bool passed = true;

                        if (task is Task<bool> boolTask)
                            if (!boolTask.Result)
                                passed = false;

                        if (task.IsFaulted)
                            passed = false;

                        if (!passed)
                        {
                            setStatus("Installation Failed!");
                            echo("One or more packages failed to install correctly! Skipping update for now...");

                            await Task
                                .Delay(500)
                                .ConfigureAwait(false);

                            return false;
                        }
                    }
                    
                    taskQueue.Clear();

                    foreach (Package package in pkgManifest)
                    {
                        if (!package.ShouldInstall)
                            continue;

                        var extract = Task.Run(() => extractPackage(package));
                        taskQueue.Add(extract);
                    }

                    await Task
                        .WhenAll(taskQueue)
                        .ConfigureAwait(true);

                    setStatus("Deleting unused files...");
                    deleteUnusedFiles();
                    
                    if (GenerateMetadata)
                    {
                        echo("Writing metadata...");

                        string pkgManifestPath = Path.Combine(studioDir, "rbxPkgManifest.txt");
                        File.WriteAllText(pkgManifestPath, pkgManifest.RawData);

                        string fileManifestPath = Path.Combine(studioDir, "rbxManifest.txt");
                        File.WriteAllText(fileManifestPath, fileManifest.RawData);

                        string versionPath = Path.Combine(studioDir, "version.txt");
                        File.WriteAllText(versionPath, versionId);

                        string versionGuidPath = Path.Combine(studioDir, "version-guid.txt");
                        File.WriteAllText(versionGuidPath, buildVersion);

                        echo("Dumping API...");

                        string studioPath = GetLocalStudioPath();
                        string apiPath = Path.Combine(studioDir, "API-Dump.json");

                        var dumpApi = Process.Start(studioPath, $"-API \"{apiPath}\"");
                        dumpApi.WaitForExit();
                    }

                    ProgressBarStyle = ProgressBarStyle.Marquee;

                    if (mainState == Program.State)
                        mainState.Channel = Channel;

                    versionRegistry.Version = versionId;
                    versionRegistry.VersionGuid = buildVersion;
                    versionRegistry.VersionOverload = targetVersion;
                }
                else
                {
                    ProgressBarStyle = ProgressBarStyle.Marquee;
                    echo("Update cancelled. Launching on current branch and version.");
                }
            }
            else
            {
                echo("This version of Roblox Studio has been installed!");
            }

            using (var http = new WebClient())
            {
                OAuth2Config_JSON = await http.DownloadStringTaskAsync(baseConfigUrl + "OAuth2Config.json");
                AppSettings_XML = await http.DownloadStringTaskAsync(baseConfigUrl + "AppSettings.xml");
            }
            
            string appSettings = Path.Combine(studioDir, "AppSettings.xml");
            string oAuth2Config = Path.Combine(studioDir, "ApplicationConfig", "OAuth2Config.json");

            echo("Writing AppSettings.xml...");
            File.WriteAllText(appSettings, AppSettings_XML);

            echo("Writing OAuth2Config.json...");
            getDirectory(studioDir, "ApplicationConfig");
            File.WriteAllText(oAuth2Config, OAuth2Config_JSON);

            // Only update the registry protocols if the main registry
            // is the global one assigned to the program itself.

            if (mainState == Program.State)
            {
                setStatus("Configuring Roblox Studio...");
                echo("Updating registry protocols...");

                Program.UpdateStudioRegistryProtocols();
            }

            if (ApplyModManagerPatches && string.IsNullOrEmpty(OverrideStudioDirectory))
            {
                echo("Applying flag configuration...");
                FlagEditor.ApplyFlags();

                echo("Patching explorer icons...");

                await ClassIconEditor
                    .PatchExplorerIcons()
                    .ConfigureAwait(true);

                // Secret feature only for me :(
                // Feel free to patch in your own thing if you want.

#               if ROBLOX_INTERNAL
                var rbxInternal = Task.Run(() => RobloxInternal.Patch(this));
                await rbxInternal.ConfigureAwait(false);
#               endif
            }

            setStatus("Starting Roblox Studio...");
            echo("Roblox Studio is up to date!");

            if (SetStartEvent)
            {
                _ = Task.Run(async () =>
                {
                    var start = new SystemEvent(StartEvent);

                    bool started = await start
                        .WaitForEvent()
                        .ConfigureAwait(true);

                    start.Close();

                    if (started)
                    {
                        var delay = Task.Delay(3000);
                        await delay.ConfigureAwait(false);

                        Application.Exit();
                    }

                    start.Dispose();
                });
            }

            return true;
        }
    }
}

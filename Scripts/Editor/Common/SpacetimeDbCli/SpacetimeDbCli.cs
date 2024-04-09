using System;
using UnityEngine;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Debug = UnityEngine.Debug;

namespace SpacetimeDB.Editor
{
    /// Common CLI action helper for UI Builder windows.
    /// - Vanilla: Do the action -> return the result -> no more.
    /// - Technically anything here accepts a CancellationToken; just add to arg + pass along!
    /// - (!) Looking for more actions? See `SpacetimeDbPublisherCli.cs`
    public static class SpacetimeDbCli
    {
        #region Static state/opts
        private const CliLogLevel CLI_LOG_LEVEL = CliLogLevel.Info;
        
        public enum CliLogLevel
        {
            Info,
            Error,
        }

        /// If you *just* installed SpacetimeDB CLI, this will update PATH in the spawned Process.
        /// Prevents restarting Unity to refresh paths (UX).
        public static string NewlyInstalledCliEnvDirPath { get; set; }

        private static bool _autoResolvedBugIsTryingAgain;
        #endregion // Static state/opts

        
        #region Init
        /// Install the SpacetimeDB CLI | https://spacetimedb.com/install 
        public static async Task<InstallSpacetimeDbCliResult> InstallSpacetimeCliAsync()
        {
            if (CLI_LOG_LEVEL == CliLogLevel.Info)
            {
                Debug.Log("Installing SpacetimeDB CLI tool...");
            }

            string argSuffix = null;
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor:
                    argSuffix = "powershell -Command \"iwr https://windows.spacetimedb.com -UseBasicParsing | iex\"\n";
                    break;
                
                case RuntimePlatform.OSXEditor:
                    argSuffix = "brew install clockworklabs/tap/spacetime";
                    break;
                
                case RuntimePlatform.LinuxEditor:
                    break;
                
                default:
                    throw new NotImplementedException("Unsupported OS");
            }
            
            SpacetimeCliResult cliResult = await runCliCommandAsync(argSuffix);
            
            if (CLI_LOG_LEVEL == CliLogLevel.Info)
            {
                Debug.Log($"Installed spacetimeDB CLI tool | {PublisherMeta.DOCS_URL}");
            }

            InstallSpacetimeDbCliResult installResult = new(cliResult);
            
            // Update PATH env var override to prevent having to restart Unity for the next cmd
            if (installResult.IsInstalled)
            {
                NewlyInstalledCliEnvDirPath = installResult.GetNormalizedPathToSpacetimeDir();
            }
            
            return installResult;
        }
        #endregion // Init
        
        
        #region Core CLI

        private static Process createCliProcess(
            string terminal,
            string fullParsedArgs,
            bool detachedProcess = false)
        {
            Process cliProcess = new();
            
            ProcessStartInfo startInfo = cliProcess.StartInfo;
            startInfo.FileName = terminal;
            startInfo.Arguments = fullParsedArgs;
            startInfo.CreateNoWindow = true;

            if (detachedProcess)
            {
                startInfo.RedirectStandardError = false;
                startInfo.RedirectStandardOutput = false;
                startInfo.UseShellExecute = true;
            }
            else
            {
                // Redirect I/O to Unity
                startInfo.RedirectStandardError = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.UseShellExecute = false;
            }
            
            // If we *just* installed SpacetimeDB CLI, PATH is not yet refreshed
            checkUpdatePathForNewSpacetimeDbCliInstall(startInfo);

            return cliProcess;
        }

        /// If we *just* installed SpacetimeDB CLI (NewlyInstalledCliEnvDirPath),
        /// PATH is not yet refreshed: Update PATH env vars
        private static void checkUpdatePathForNewSpacetimeDbCliInstall(ProcessStartInfo startInfo)
        {
            bool hasEnvPathOverride = !string.IsNullOrEmpty(NewlyInstalledCliEnvDirPath);
            if (!hasEnvPathOverride)
            {
                return;
            }
        
            string keyName = Application.platform == RuntimePlatform.WindowsEditor ? "Path" : "PATH"; 
            string pathAddendum = Path.PathSeparator + NewlyInstalledCliEnvDirPath;
            startInfo.EnvironmentVariables[keyName] += pathAddendum;
        }
        
        private static void logInput(string terminal, string fullParsedArgs)
        {
            if (CLI_LOG_LEVEL != CliLogLevel.Info)
            {
                return;
            }
                
            Debug.Log($"CLI Input: `<color=yellow>{terminal} {fullParsedArgs}</color>`\n");
        }
        
        /// Starts a detached Process, allowing a domain reload to not freeze Unity or kill the process.
        public static void startDetachedCliProcess(string argSuffix)
        {
            // Args
            string terminal = getTerminalPrefix(); // Determine terminal based on platform
            string argPrefix = getCommandPrefix(); // Determine command prefix (cmd /c, etc.)
            string fullParsedArgs = $"{argPrefix} \"{argSuffix}\"";
            
            // Process + StartInfo
            Process asyncCliProcess = createCliProcess(terminal, fullParsedArgs, detachedProcess: true);
            logInput(terminal, fullParsedArgs);
            
            try
            {
                asyncCliProcess.Start();
                asyncCliProcess.Dispose();
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to {nameof(startDetachedCliProcess)}: {e.Message}");
                throw;
            }
        }

        // /// Issue a cross-platform *async* (EnableRaisingEvents) CLI cmd, where we'll start with terminal prefixes
        // /// as the CLI "command" and some arg prefixes for compatibility.
        // /// This particular overload is useful for streaming logs or running a cancellable listen server in the background
        // /// For async CLI calls, a CancellationToken is *required* (create a CancellationTokenSource)
        // /// (!) YOU MANUALLY START THIS PROCESS (to give you a chance to subscribe to logs early)
        // /// - Usage: asyncCliResult.Start() 
        // [Obsolete("BUG: Domain refresh will both kill the process and freeze Unity")]
        // public static Task<SpacetimeStreamingCliResult> runStreamingCliCommand(
        //     string argSuffix,
        //     bool stopOnError,
        //     CancellationTokenSource cancelTokenSrc)
        // {
        //     // Args
        //     string terminal = getTerminalPrefix(); // Determine terminal based on platform
        //     string argPrefix = getCommandPrefix(); // Determine command prefix (cmd /c, etc.)
        //     string fullParsedArgs = $"{argPrefix} \"{argSuffix}\"";
        //     
        //     // Process + StartInfo
        //     Process asyncCliProcess = createCliProcess(terminal, fullParsedArgs, isAsync: true);
        //
        //     // Cancellation Token + Async CLI Result (set early to prepare streaming logs *before* proc start)
        //     SpacetimeStreamingCliRequest streamingCliRequest = new(
        //         asyncCliProcess, 
        //         stopOnError, 
        //         continueFollowingLogsAfterDone: true, 
        //         cancelTokenSrc);
        //     
        //     SpacetimeStreamingCliResult streamingCliResult = new(streamingCliRequest);
        //     
        //     // TODO: Show later just before we start the proc @ SpacetimeAsyncCliResult?
        //     logInput(terminal, fullParsedArgs);
        //     
        //     // (!) Manually start this proc when you want
        //     return Task.FromResult(streamingCliResult);
        // }
        
        /// Issue a cross-platform CLI cmd, where we'll start with terminal prefixes
        /// as the CLI "command" and some arg prefixes for compatibility.
        /// Usage: Pass an argSuffix, such as "spacetime version",
        ///        along with an optional cancel token
        /// - Supports cancellations and timeouts via CancellationToken (create a CancellationTokenSource)
        /// TODO: runInBackground, with SpacetimeAsyncCliResult, probably supports streaming (--follow) logs: give it a shot!
        public static async Task<SpacetimeCliResult> runCliCommandAsync(
            string argSuffix,
            CancellationToken cancelToken = default,
            bool runInBackground = false)
        {
            // Args
            string terminal = getTerminalPrefix(); // Determine terminal based on platform
            string argPrefix = getCommandPrefix(); // Determine command prefix (cmd /c, etc.)
            string fullParsedArgs = $"{argPrefix} \"{argSuffix}\"";
            
            // Process + StartInfo
            Process cliProcess = createCliProcess(terminal, fullParsedArgs);

            // Cancellation Token + Async CLI Result (set early to prepare streaming logs *before* proc start)
            CancellationTokenRegistration cancellationRegistration = default;
            logInput(terminal, fullParsedArgs);
            
            // Set output+err logs just once after done (!async)
            string output = string.Empty;
            string error = string.Empty;

            try
            {
                cliProcess.Start();

                // Register cancellation token to safely handle process termination
                cancellationRegistration = cancelToken.Register(() => terminateProcessSafely(cliProcess));

                // Asynchronously read output and error
                Task<string> readOutputTask = cliProcess.StandardOutput.ReadToEndAsync();
                Task<string> readErrorTask = cliProcess.StandardError.ReadToEndAsync();

                // Wait for the process to exit or be cancelled
                if (!runInBackground)
                {
                    while (!cliProcess.HasExited)
                        await Task.Delay(100, cancelToken);    
                }

                // Await the read tasks to ensure output and error are captured
                output = await readOutputTask;
                error = await readErrorTask;
            }
            catch (OperationCanceledException)
            {
                Debug.LogWarning("CLI Warning: Canceled");
                error = "Canceled";
            }
            catch (Exception e)
            {
            }
            finally
            {
                // Heavy cleanup
                await cancellationRegistration.DisposeAsync();
                if (!cliProcess.HasExited)
                {
                    cliProcess.Kill();
                }

                cliProcess.Dispose(); // No async ver for this Dispose
            }
            
            // Process results, log err (if any), return parsed Result 
            SpacetimeCliResult cliResult = new(output, error);
            logCliResults(cliResult);

            // Can we auto-resolve this issue and try again?
            if (cliResult.HasCliErr && cliResult.CliError != "Canceled" && !_autoResolvedBugIsTryingAgain)
            {
                bool isResolvedTryAgain = await autoResolveCommonCliErrors(cliResult);
                if (isResolvedTryAgain)
                {
                    return await runCliCommandAsync(argSuffix, cancelToken);
                }
            }
            _autoResolvedBugIsTryingAgain = false;
            
            return cliResult;
        }

        /// <summary>
        /// Attempts to detect + resolve common cli errors:
        /// 1. "Error: Cannot list identities for server without a saved fingerprint: local"
        /// </summary>
        /// <returns>isResolvedTryAgain</returns>
        private static async Task<bool> autoResolveCommonCliErrors(SpacetimeCliResult cliResult)
        {
            // TODO: Break this up if we catch too many
            _autoResolvedBugIsTryingAgain = true;
            bool isLocalFingerprintErr = cliResult.CliError.Contains("without a saved fingerprint: local");
            bool serverOffline = cliResult.CliError.Contains("target machine actively refused");

            if (!serverOffline && !isLocalFingerprintErr)
            {
                return false; // !isResolvedTryAgain
            }
            
            // Ensure the local server is running
            PingServerResult pingResult = await PingServerAsync();
            if (!pingResult.IsServerOnline)
            {
                 pingResult = await StartDetachedLocalServer(); // Temporarily start the server
            }
            
            // Attempt to create a fingerprint for the server
            SpacetimeCliResult fingerprintResult = await createFingerprintAsync(SpacetimeMeta.LOCAL_SERVER_NAME);
            // _ = ForceStopLocalServerAsync(); // No need to await this // Or perhaps it's good to keep it running?

            bool isResolvedTryAgain = !fingerprintResult.HasCliErr;
            return isResolvedTryAgain;
        }

        public static void terminateProcessSafely(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(5000)) // Wait up to 5 seconds
                    {
                        process.Kill(); // Force terminate the process if it hasn't exited
                    }
                }
            }
            catch (InvalidOperationException e)
            {
                // Process likely already exited or been disposed; safe to ignore, in most cases
                Debug.LogWarning($"Attempted to terminate a process: {e.Message}");
            }
        }

        public static void logCliResults(SpacetimeCliResult cliResult)
        {
            bool hasOutput = !string.IsNullOrEmpty(cliResult.CliOutput);
            bool hasLogLevelInfoNoErr = CLI_LOG_LEVEL == CliLogLevel.Info && !cliResult.HasCliErr;
            string prettyOutput = $"\n```\n<color=yellow>{cliResult.CliOutput}</color>\n```\n";

            if (hasOutput && hasLogLevelInfoNoErr)
            {
                Debug.Log($"CLI Output: {prettyOutput}");
            }

            if (cliResult.HasCliErr)
            {
                // There may be only a CliError and no CliOutput, depending on the type of error.
                if (!string.IsNullOrEmpty(cliResult.CliOutput))
                {
                    Debug.Log($"CLI Output: {prettyOutput}");
                }

                Debug.LogError($"CLI Error: {cliResult.CliError}");

                // Separate the errs found from the CLI output so the user doesn't need to dig
                bool logCliResultErrsSeparately = cliResult.ErrsFoundFromCliOutput?.Count is > 0 and < 5;
                
                if (cliResult.HasErrsFoundFromCliOutput & logCliResultErrsSeparately) // If not too many
                {
                    for (int i = 0; i < cliResult.ErrsFoundFromCliOutput.Count; i++)
                    {
                        string err = cliResult.ErrsFoundFromCliOutput[i];
                        Debug.LogError($"CLI Error Summary[{i}]: {err}");
                    }
                }
            }
        }
        
        public static string getCommandPrefix()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor:
                    return "/c";
                
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.LinuxEditor:
                    return "-c";
                
                default:
                    Debug.LogError("Unsupported OS");
                    return null;
            }
        }

        /// Return either "cmd.exe" || "/bin/bash"
        public static string getTerminalPrefix()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor:
                    return "cmd.exe";
                
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.LinuxEditor:
                    return "/bin/bash";
                
                default:
                    Debug.LogError("Unsupported OS");
                    return null;
            }
        }
        #endregion // Core CLI
        
        
        #region CLI Utils
        /// Cross-platform kill cmd for SpacetimeDB Local Server (or technically any port)
        /// TODO: Needs +review for safety; ran through ChatGPT a couple times
        private static string getKillCommand(ushort port)
        {
            bool isWin = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows);
            
            // For safety, ensures pid != 0
            if (isWin)
            {
                // Windows command to find by port and kill
                return $"netstat -aon | findstr :{port} && for /f \"tokens=5\" %a " +
                       $"in ('netstat -aon ^| findstr :{port}') " +
                       $"do if not %a==0 taskkill /F /PID %a";
            }
            
            // macOS/Linux command to find by port and kill
            // TODO: Mac|Linux needs testing
            return $"lsof -ti:{port} | grep -v '^0$' | xargs -r kill -9";
        }
        #endregion // CLI Utils
        

        #region High Level CLI Actions
        /// isInstalled = !cliResult.HasCliError 
        public static async Task<SpacetimeCliResult> GetIsSpacetimeCliInstalledAsync()
        {
            string argSuffix = "spacetime version";
            SpacetimeCliResult cliResult = await runCliCommandAsync(argSuffix);
            return cliResult;
        }
        
        /// Uses the `spacetime identity list` CLI command
        public static async Task<GetIdentitiesResult> GetIdentitiesAsync()
        {
            string argSuffix = "spacetime identity list";
            SpacetimeCliResult cliResult = await runCliCommandAsync(argSuffix);
            GetIdentitiesResult getIdentitiesResult = new(cliResult);
            return getIdentitiesResult;
        }
        
        /// Uses the `spacetime identity list` CLI command
        public static async Task<GetServersResult> GetServersAsync() 
        {
            string argSuffix = "spacetime server list";
            SpacetimeCliResult cliResult = await runCliCommandAsync(argSuffix);
            GetServersResult getServersResult = new(cliResult);
            return getServersResult;
        }
        
        /// Uses the `spacetime list {identity}` CLI command.
        /// (!) This only returns the addresses.
        ///     For nicknames, see the chained call: GetDbAddressesWithNicknames
        public static async Task<GetDbAddressesResult> GetDbAddressesAsync(string identity)
        {
            string argSuffix = $"spacetime list {identity}";
            SpacetimeCliResult cliResult = await runCliCommandAsync(argSuffix);
            GetDbAddressesResult getDbAddressesResult = new(cliResult);
            return getDbAddressesResult;
        }
        
        /// [Slow] Uses the `spacetime describe {moduleName} [--as-identity {identity}]` CLI command
        public static async Task<GetEntityStructureResult> GetEntityStructureAsync(
            string moduleName,
            string asIdentity = null)
        {
            // Append ` --as-identity {identity}`?
            string asIdentitySuffix = string.IsNullOrEmpty(asIdentity) ? "" : $" --as-identity {asIdentity}";
            string argSuffix = $"spacetime describe {moduleName}{asIdentitySuffix}";
            
            SpacetimeCliResult cliResult = await runCliCommandAsync(argSuffix);
            GetEntityStructureResult getEntityStructureResult = new(cliResult);
            return getEntityStructureResult;
        }

        /// Uses the `spacetime logs` CLI command.
        /// <param name="serverName"></param>
        public static async Task<SpacetimeCliResult> GetLogsAsync(string serverName)
        {
            string argSuffix = $"spacetime logs {serverName}";
            SpacetimeCliResult cliResult = await runCliCommandAsync(argSuffix);
            return cliResult;
        }
        
        /// <summary>Uses the `spacetime server ping` CLI command.</summary>
        /// <param name="cancelToken">If left default, set to 200ms timeout</param>
        public static async Task<PingServerResult> PingServerAsync(CancellationToken cancelToken = default)
        {
            if (cancelToken == default)
            {
                cancelToken = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)).Token;
            }
            
            const string argSuffix = "spacetime server ping";
            SpacetimeCliResult cliResult = await runCliCommandAsync(argSuffix, cancelToken);
            PingServerResult pingServerResult = new(cliResult);
            return pingServerResult;
        }

        /// <param name="cancelToken">If left default, set to 200ms timeout (2 attempts @ 1 per 100ms)</param>
        public static async Task<PingServerResult> PingServerUntilOnlineAsync(CancellationToken cancelToken = default)
        {
            bool isOnline = false;
            
            // If default, set to 200ms timeout
            using CancellationTokenSource globalTimeoutCts = cancelToken == default
                ? new CancellationTokenSource(TimeSpan.FromMilliseconds(200)) 
                : CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
            
            try
            {
                while (!globalTimeoutCts.Token.IsCancellationRequested)
                {
                    using CancellationTokenSource pingIterationCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                    try
                    {
                        // Attempt to ping the server with a per-iteration timeout
                        PingServerResult pingServerResult = await PingServerAsync(pingIterationCts.Token);
                        isOnline = !pingServerResult.HasCliErr;

                        if (isOnline)
                        {
                            return pingServerResult;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // If the ping iteration was cancelled, we simply continue to the next iteration until global timeout.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Global timeout cancellation: Timed out!
            }

            // Timed out
            return new PingServerResult(new SpacetimeCliResult("", "Canceled"));
        }
        
        /// Uses the `spacetime start` CLI command. Runs in background in a detached service.
        /// Awaits up to 2s for the server to come online.
        public static async Task<PingServerResult> StartDetachedLocalServer()
        {
            const string argSuffix = "spacetime start";
            startDetachedCliProcess(argSuffix);
            
            // Await success, pinging the CLI every 100ms to ensure online. Max 2 seconds.
            return await PingServerUntilOnlineAsync();
        }
        
        /// Cross-platform kills process by port num (there's no universal `stop` command)
        public static async Task<SpacetimeCliResult> ForceStopLocalServerAsync(ushort port = SpacetimeMeta.DEFAULT_PORT)
        {
            string argSuffix = getKillCommand(port);
            SpacetimeCliResult cliResult = await runCliCommandAsync(argSuffix);
            return cliResult;
        }
        #endregion // High Level CLI Actions
        
        
        #region // Low Level CLI Actions
        /// <summary>Uses the `spacetime server ping` CLI command.</summary>
        /// <param name="serverName">This is most likely "local" || "testnet"</param>
        private static async Task<SpacetimeCliResult> createFingerprintAsync(string serverName)
        {
            string argSuffix = $"spacetime server fingerprint {serverName} --force";
            SpacetimeCliResult cliResult = await runCliCommandAsync(argSuffix);
            return cliResult;
        }
        #endregion // Low Level CLI Actions

    }
}
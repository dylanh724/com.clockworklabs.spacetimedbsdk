using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.UIElements;
using static SpacetimeDB.Editor.PublisherMeta;
using static SpacetimeDB.Editor.SpacetimeWindow;

namespace SpacetimeDB.Editor
{
    /// Unlike PublisherWindowCallbacks, these are not called *directly* from UI.
    /// Runs an action -> Processes isSuccess -> calls success || fail @ PublisherWindowCallbacks.
    /// PublisherWindowCallbacks should handle try/catch (except for init chains).
    public partial class PublisherWindow
    {
        #region Init from PublisherWindow.CreateGUI
        /// Installs CLI tool, shows identity dropdown, gets identities.
        /// Initially called by PublisherWindow @ CreateGUI.
        /// !autoProgress so we can better see init order here, manually
        private async Task initDynamicEventsFromPublisherWindow()
        {
            await startTests(); // Only if PublisherWindowTester.PUBLISH_WINDOW_TESTS
            await ensureSpacetimeCliInstalledAsync(); // installSpacetimeDbCliAsync() => onInstallSpacetimeDbCliSuccess()
            await getServersSetDropdown(autoProgressIdentities: false);
            bool revealedIdentityFoldout = await revealIdentitiesGroupIfNotOfflineLocalServerAsync();
            if (!revealedIdentityFoldout)
            {
                return;
            }
            
            await getIdentitiesSetDropdown(autoProgressPublisher: false);

            bool selectedIdentity = identitySelectedDropdown.index >= 0;
            bool identitySelectedAndVisible = selectedIdentity && IsShowingUi(identitySelectedDropdown);
            if (identitySelectedAndVisible)
            {
                await revealPublishGroupAndResultCache();
            }
        }
        
        /// Initially called by PublisherWindow @ CreateGUI
        /// - Set to the initial state as if no inputs were set.
        /// - This exists so we can show all ui elements simultaneously in the
        ///   ui builder for convenience.
        /// - (!) If called from CreateGUI, after a couple frames,
        ///       any persistence from `ViewDataKey`s may override this.
        private void resetUi()
        {
            resetInstallCli();
            resetServer();
            resetIdentity();
            resetPublish();
            resetLocalServerGroup();
            resetPublishResultCache();
            clearLabels();
            
            // Hide all foldouts and labels from Identity+ (show Server)
            toggleFoldoutRipple(startRippleFrom: FoldoutGroupType.Identity, show:false);
        }

        private void resetLocalServerGroup()
        {
            HideUi(publishLocalBtnsHoriz);
            HideUi(publishStartLocalServerBtn);
            HideUi(publishStopLocalServerBtn);
            HideUi(serverConnectingStatusLabel);
        }

        private void resetPublish()
        {
            // Hide publish
            HideUi(publishFoldout);
            HideUi(publishGroupBox);
            HideUi(publishCancelBtn);
            FadeOutUi(publishStatusLabel);

            if (_progressBarCts is { IsCancellationRequested: true })
            {
                hideProgressBarAndCancel(publishInstallProgressBar);
            }
            else
            {
                HideUi(publishInstallProgressBar);
            }
            
            resetPublishAdvanced();
        }

        private void resetPublishAdvanced()
        {
            publishModuleDebugModeToggle.SetEnabled(false);
            publishModuleDebugModeToggle.value = false;
            publishModuleClearDataToggle.value = false;
        }

        private void resetIdentity()
        {
            HideUi(identityAddNewShowUiBtn);
            HideUi(identityNewGroupBox);
            resetIdentityDropdown();
            identitySelectedDropdown.value = SpacetimeMeta.GetStyledStr(
                SpacetimeMeta.StringStyle.Action, "Discovering ..."); 
            identityAddBtn.SetEnabled(false);
        }

        private void resetServer()
        {
            HideUi(publishLocalBtnsHoriz);
            HideUi(serverAddNewShowUiBtn);
            HideUi(serverNewGroupBox);
            serverNicknameTxt.value = "";

            serverHostTxt.value = "";
            serverHostTxt.isReadOnly = false;
            
            resetServerDropdown();
        }

        private void resetInstallCli()
        {
            HideUi(installCliGroupBox);
            hideProgressBarAndCancel(installCliProgressBar);
            HideUi(installCliStatusLabel);
        }

        /// Check for install => Install if !found -> Throw if err
        private async Task ensureSpacetimeCliInstalledAsync()
        {
            // Check if Spacetime CLI is installed => install, if !found
            SpacetimeCliResult cliResult = await SpacetimeDbCliActions.GetIsSpacetimeCliInstalledAsync();
            
            // Process result -> Update UI
            bool isSpacetimeCliInstalled = !cliResult.HasCliErr;
            if (isSpacetimeCliInstalled)
            {
                onSpacetimeCliAlreadyInstalled();
                return;
            }
            
            await installSpacetimeDbCliAsync();
        }

        private void setInstallSpacetimeDbCliUi()
        {
            // Command !found: Update status => Install now
            _ = startProgressBarAsync(
                installCliProgressBar, 
                barTitle: "Installing SpacetimeDB CLI ...",
                initVal: 4,
                valIncreasePerSec: 4,
                autoHideOnComplete: true);

            HideUi(installCliStatusLabel);
            ShowUi(installCliGroupBox);
        }

        private async Task installSpacetimeDbCliAsync()
        {
            setInstallSpacetimeDbCliUi();
            
            // Run CLI cmd
            InstallSpacetimeDbCliResult installResult = await SpacetimeDbCli.InstallSpacetimeCliAsync();
            
            // Process result -> Update UI
            bool isSpacetimeDbCliInstalled = installResult.IsInstalled;
            if (!isSpacetimeDbCliInstalled)
            {
                // Critical error: Spacetime CLI !installed and failed install attempt
                onInstallSpacetimeDbCliFail();
                return;
            }
            
            await onInstallSpacetimeDbCliSuccess();
        }

        /// Normally, we need to restart Unity to env vars (since child Processes use Unity's launched env vars),
        /// but we have a special env var injection when using SpacetimeDBCli that inits after 1st install that
        /// persists until Unity is closed (workaround) 
        private async Task onInstallSpacetimeDbCliSuccess()
         {
            // Validate install success
            installCliProgressBar.title = "Validating SpacetimeDB CLI Installation ...";
            
            SpacetimeCliResult validateCliResult = await SpacetimeDbCliActions.GetIsSpacetimeCliInstalledAsync();

            bool isNotRecognizedCmd = validateCliResult.HasCliErr && 
                SpacetimeDbCli.CheckCmdNotFound(validateCliResult.CliError, expectedCmd: "spacetime");
            
            if (isNotRecognizedCmd)
            {
                // ########################################################################################
                // (!) This err below technically shouldn't happen anymore due to the env var workaround,
                //     but *just in case*
                // ########################################################################################
                // This is only a "partial" error: We probably installed, but the env vars didn't refresh
                // We need to restart Unity to refresh the spawned child Process env vars since manual refresh failed
                onInstallSpacetimeDbCliSoftFail(); // Throws
                return;
            }
            
            // Set default fingerprint for testnet -- not yet local, since that requires a local server *running* 
            await newInstallSetTestnetFingerpintAsync();
            
            // Set `testnet` as default server (currently `local`)
            await newInstallSetTestnetAsDefaultServerAsync();

            HideUi(installCliGroupBox);
        }

        /// It's actually faster to just set default than to 1st check if it's already set: So we just set
        private async Task newInstallSetTestnetAsDefaultServerAsync()
        {
            Debug.Log($"[{nameof(onInstallSpacetimeDbCliSuccess)}] Setting default server to `testnet` ...");
            SpacetimeCliResult cliResult = await SpacetimeDbPublisherCliActions
                .SetDefaultServerAsync(SpacetimeMeta.TESTNET_SERVER_NAME);
            
            bool isSuccess = !cliResult.HasCliErr;
            if (!isSuccess)
            {
                throw new Exception("Failed to set default server to 'testnet' after a new install");
            }
        }

        private async Task newInstallSetTestnetFingerpintAsync()
        {
            Debug.Log($"[{nameof(onInstallSpacetimeDbCliSuccess)}] Setting default `testnet` fingerprint ...");
            SpacetimeCliResult cliResult = await SpacetimeDbCliActions
                .CreateFingerprintAsync(SpacetimeMeta.TESTNET_SERVER_NAME);

            if (cliResult.HasCliErr)
            {
                throw new Exception($"Failed to set default fingerprint for `testnet`: {cliResult.CliError}");
            }
        }

        /// Set common fail UI, shared between hard and soft fail funcs
        private void onInstallSpacetimeDbFailUi()
        {
            ShowUi(installCliStatusLabel);
            ShowUi(installCliGroupBox);
            hideProgressBarAndCancel(installCliProgressBar);
        }

        /// Technically success, but we need to restart Unity to refresh PATH env vars
        /// Throws to prevent further execution in the init chain
        /// (!) This technically shouldn't happen anymore due to the env var workaround,
        ///     but *just in case*
        private void onInstallSpacetimeDbCliSoftFail()
        {
            onInstallSpacetimeDbFailUi();
            
            // TODO: Cross-platform refresh env vars without having to restart Unity (surprisingly advanced)
            string successButRestartMsg = "<b>Successfully Installed SpacetimeDB CLI:</b>\n" +
                "Please restart Unity to refresh the CLI env vars";

            serverSelectedDropdown.SetEnabled(false);
            serverSelectedDropdown.SetValueWithoutNotify("Awaiting PATH Update (Unity Restart)");
            installCliStatusLabel.text = SpacetimeMeta.GetStyledStr(
                SpacetimeMeta.StringStyle.Success, successButRestartMsg);

            throw new Exception("Successful install, but Unity must restart (to refresh PATH env vars)");
        }
        
        /// Throws Exception
        private void onInstallSpacetimeDbCliFail()
        {
            onInstallSpacetimeDbFailUi();

            string errMsg = "<b>Failed to Install Spacetime CLI:</b>\nSee logs";
            installCliStatusLabel.text = SpacetimeMeta.GetStyledStr(
                SpacetimeMeta.StringStyle.Error, errMsg);
            
            throw new Exception(errMsg);
        }

        /// Try to get get list of Servers from CLI.
        /// This should be called at init at runtime from PublisherWIndow at CreateGUI time.
        /// autoProgress to reveal Identities group on success?
        private async Task getServersSetDropdown(bool autoProgressIdentities)
        {
            // Run CLI cmd
            GetServersResult getServersResult = await SpacetimeDbCliActions.GetServersAsync();
            
            // Process result -> Update UI
            bool isSuccess = getServersResult.HasServer;
            if (!isSuccess)
            {
                onGetSetServersFail(getServersResult);
                return;
            }
            
            // Success
            await onGetServersSetDropdownSuccess(getServersResult, autoProgressIdentities);
        }
        #endregion // Init from PublisherWindow.CreateGUI


        /// Success:
        /// - Get server list and ensure it's default
        /// - Refresh identities, since they are bound per-server
        /// autoProgress to reveal Identities group on success?
        private async Task onGetServersSetDropdownSuccess(
            GetServersResult getServersResult,
            bool autoProgress)
        {
            await onGetSetServersSuccessEnsureDefaultAsync(getServersResult.Servers, autoProgress);

            bool isLocalServerAndOffline = await pingLocalServerSetBtnsAsync();
            if (isLocalServerAndOffline)
            {
                return;
            }

            if (autoProgress)
            {
                await getIdentitiesSetDropdown(autoProgressPublisher: true); // Process and reveal the next UI group
            }
        }

        /// Try to get list of Identities from CLI. (!) Servers must already be set.
        /// autoProgress to reveal Publisher group on success?
        private async Task getIdentitiesSetDropdown(bool autoProgressPublisher)
        {
            Debug.Log($"Gathering identities for selected '{serverSelectedDropdown.value}' server...");
            
            // Sanity check: Is there a selected server?
            bool hasSelectedServer = serverSelectedDropdown.index >= 0;
            if (!hasSelectedServer)
            {
                Debug.LogError("Tried to get identities before server is selected");
                return;
            }
            
            // Run CLI cmd
            GetIdentitiesResult getIdentitiesResult = await SpacetimeDbCliActions.GetIdentitiesAsync();
            
            // Process result -> Update UI
            bool isSuccess = getIdentitiesResult.HasIdentity;
            if (!isSuccess)
            {
                onGetSetIdentitiesFail();
                return;
            }
            
            // Success
            await populateIdentitiesDropdownEnsureDefaultAsync(
                getIdentitiesResult.Identities, 
                autoProgressPublisher);
        }
        
        /// Validates if we at least have a host name before revealing
        /// bug: If you are calling this from CreateGUI, openFoldout will be ignored.
        private void revealPublishResultCacheIfHostExists(bool? openFoldout)
        {
            // Sanity check: Ensure host is set
            bool hasVal = !string.IsNullOrWhiteSpace(publishResultHostTxt.value);
            if (!hasVal)
            {
                return;
            }

            // Reveal the publishAsync result info cache
            ShowUi(publishResultFoldout);
            
            if (openFoldout != null)
            {
                publishResultFoldout.value = (bool)openFoldout;
            }
        }
        
        /// (1) Suggest module name, if empty
        /// (2) Reveal publisher group, if autoProgressPublisher (false on init)
        /// (3) Ensure spacetimeDB CLI is installed async, if autoProgressPublisher
        private async Task onPublishModulePathSetAsync(bool autoProgressPublisher)
        {
            // We just updated the path - hide old publishAsync result cache
            HideUi(publishResultFoldout);
            
            // Set the tooltip to equal the path, since it's likely cutoff
            publishModulePathTxt.tooltip = publishModulePathTxt.value;
            
            // Since we changed the path, we should wipe stale publishAsync info 
            resetPublishResultCache();
            
            // ServerModulePathTxt persists: If previously entered, show the publishAsync group
            bool hasPathSet = !string.IsNullOrEmpty(publishModulePathTxt.value);
            if (!hasPathSet || !autoProgressPublisher)
            {
                return;
            }
            
            try
            {
                // +Ensures SpacetimeDB CLI is installed async
                await revealPublisherGroupUiAsync();
            }
            catch (Exception e)
            {
                Debug.LogError(e.Message);
                throw;
            }
        }
        
        /// Dynamically sets a dashified-project-name placeholder, if empty
        private void suggestModuleNameIfEmpty()
        {
            // Set the server module name placeholder text dynamically, based on the project name
            // Replace non-alphanumeric chars with dashes
            bool hasName = !string.IsNullOrEmpty(publishModuleNameTxt.value);
            if (hasName)
            {
                return; // Keep whatever the user customized
            }

            // Generate dashified-project-name fallback suggestion
            publishModuleNameTxt.value = getSuggestedServerModuleName();
        }
        
        /// (!) bug: If NO servers are found, including the default, we'll regenerate them back.
        private void onGetSetServersFail(GetServersResult getServersResult)
        {
            if (!getServersResult.HasServer && !_isRegeneratingDefaultServers)
            {
                Debug.Log("[BUG] No servers found; defaults were wiped: " +
                    "regenerating, then trying again...");
                _isRegeneratingDefaultServers = true;
                _ = regenerateServers();         
                return;
            }
            
            // Hide dropdown, reveal new ui group
            Debug.Log("No servers found - revealing 'add new server' group");

            // UI: Reset flags, clear cohices, hide selected server dropdown box
            _isRegeneratingDefaultServers = false; // in case we looped around to a fail
            serverSelectedDropdown.choices.Clear();
            HideUi(serverSelectedDropdown);
            
            // Show "add new server" group box, focus nickname
            ShowUi(serverNewGroupBox);
            serverNicknameTxt.Focus();
            serverNicknameTxt.SelectAll();
        }

        /// When local and testnet are missing, it's 99% due to a bug:
        /// We'll add them back. Assuming default ports (3000) and testnet targets.
        /// `testnet` will become default 
        private async Task regenerateServers()
        {
            Debug.Log("Regenerating default servers: [ local, testnet* ] *Becomes default");
            
            // UI
            serverConnectingStatusLabel.text = SpacetimeMeta.GetStyledStr(
                SpacetimeMeta.StringStyle.Action, 
                "<b>Regenerating default servers:</b>\n[ local, testnet* ]");
            ShowUi(serverConnectingStatusLabel);

            AddServerRequest addServerRequest = null;
            
            // Run CLI cmd: Add `local` server (forces `--no-fingerprint` so it doesn't need to be running now)
            addServerRequest = new(SpacetimeMeta.LOCAL_SERVER_NAME, SpacetimeMeta.LOCAL_HOST_URL);
            _ = await SpacetimeDbPublisherCliActions.AddServerAsync(addServerRequest);
            
            // Run CLI cmd: Add `testnet` server (becomes default)
            addServerRequest = new(SpacetimeMeta.TESTNET_SERVER_NAME, SpacetimeMeta.TESTNET_HOST_URL);
            _ = await SpacetimeDbPublisherCliActions.AddServerAsync(addServerRequest);
            
            // Success - try again
            _ = getServersSetDropdown(autoProgressIdentities: true);
        }

        private void onGetSetIdentitiesFail()
        {
            // Hide dropdown, reveal new ui group
            Debug.Log("No identities found - revealing 'add new identity' group");
            
            // UI: Reset choices, hide dropdown+new identity btn
            identitySelectedDropdown.choices.Clear();
            HideUi(identitySelectedDropdown);
            HideUi(identityAddNewShowUiBtn);
            
            // UI: Reveal "add new identity" group, reveal foldout
            ShowUi(identityNewGroupBox);
            ShowUi(identityFoldout);
            
            // UX: Focus Nickname field
            identityNicknameTxt.Focus();
            identityNicknameTxt.SelectAll();
            
            // We shouldn't show anything below
            toggleFoldoutRipple(FoldoutGroupType.Publish, show: false);
        }

        /// Works around UI Builder bug on init that will add the literal "string" type to [0]
        private void resetIdentityDropdown()
        {
            identitySelectedDropdown.choices.Clear();
            identitySelectedDropdown.value = "";
            identitySelectedDropdown.index = -1;
        }
        
        /// Works around UI Builder bug on init that will add the literal "string" type to [0]
        private void resetServerDropdown()
        {
            serverSelectedDropdown.choices.Clear();
            serverSelectedDropdown.value = "";
            serverSelectedDropdown.index = -1;
            serverSelectedDropdown.value = SpacetimeMeta.GetStyledStr(
                SpacetimeMeta.StringStyle.Action, "Discovering ...");
            
            HideUi(serverConnectingStatusLabel);
        }
        
        /// Set the selected identity dropdown. If identities found but no default, [0] will be set.
        /// autoProgress to reveal Publisher group on success?
        private async Task populateIdentitiesDropdownEnsureDefaultAsync(
            List<SpacetimeIdentity> identities, 
            bool autoProgressPublisher)
        {
            // Logs for each found, with default shown
            foreach (SpacetimeIdentity identity in identities)
                Debug.Log($"Found identity: {identity}");
            
            // Setting will trigger the onIdentitySelectedDropdownChangedAsync event @ PublisherWindow
            foreach (SpacetimeIdentity identity in identities)
            {
                identitySelectedDropdown.choices.Add(identity.Nickname);

                if (identity.IsDefault)
                {
                    // Set the index to the most recently-added one
                    int recentlyAddedIndex = identitySelectedDropdown.choices.Count - 1;
                    identitySelectedDropdown.index = recentlyAddedIndex;
                }
            }
            
            // Ensure a default was found
            bool foundIdentity = identities.Count > 0;
            bool foundDefault = identitySelectedDropdown.index >= 0;
            if (foundIdentity && !foundDefault)
            {
                Debug.LogError("Found Identities, but no default " +
                    $"Falling back to [0]:{identities[0].Nickname} and setting via CLI...");
                identitySelectedDropdown.index = 0;
            
                // We need a default identity set
                string nickname = identities[0].Nickname;
                await setDefaultIdentityAsync(nickname);
            }

            // Process result -> Update UI
            await onEnsureIdentityDefaultSuccessAsync(autoProgressPublisher);
        }
        
        /// autoProgress to reveal Publisher group on success?
        private async Task onEnsureIdentityDefaultSuccessAsync(bool autoProgressPublisher)
        {
            // Allow selection, show [+] identity new reveal ui btn
            identitySelectedDropdown.pickingMode = PickingMode.Position;
            ShowUi(identityAddNewShowUiBtn);
            
            // Hide "new id" group + status label
            HideUi(identityStatusLabel);
            HideUi(identityNewGroupBox);
            
            // Show this identity foldout + dropdown, which may have been hidden
            // if a server was recently changed
            ShowUi(identityFoldout);
            ShowUi(identitySelectedDropdown);

            _foundIdentity = true;
            
            // Show the next section+? +UX: Focus the 1st field
            if (!autoProgressPublisher)
            {
                return;
            }

            await revealPublishGroupAndResultCache();
        }

        private async Task revealPublishGroupAndResultCache()
        {
            await revealPublisherGroupUiAsync();
            
            // If we have a cached result, show that (minimized)
            revealPublishResultCacheIfHostExists(openFoldout: false);
        }

        /// <returns>shouldEnablePubBtn</returns>
        private bool checkShouldEnablePublishBtn()
        {
            // Is local server && offline?
            if (checkIsLocalhostServerSelected() && publishStatusLabel.text.ToLowerInvariant().Contains("offline"))
            {
                return false; // !shouldEnablePubBtn
            }
            
            bool hasPubModuleName = !string.IsNullOrEmpty(publishModuleNameTxt.value);
            bool hasPubModulePath = !string.IsNullOrEmpty(publishModulePathTxt.value);
            return hasPubModuleName && hasPubModulePath; // shouldEnablePubBtn
        }

        /// Only allow --debug for !localhost (for numerous reasons, including a buffer overload bug)
        /// Always false if called from init (since it will be "Discovering ...")
        private void toggleDebugModeIfNotLocalhost()
        {
            bool isLocalhost = checkIsLocalhostServerSelected();
            publishModuleDebugModeToggle.SetEnabled(isLocalhost);
        }

        /// Set the selected server dropdown. If servers found but no default, [0] will be set.
        /// Also can be called by OnAddServerSuccess by passing a single server
        private async Task onGetSetServersSuccessEnsureDefaultAsync(
            List<SpacetimeServer> servers, 
            bool autoProgressIdentities)
        {
            // Logs for each found, with default shown
            foreach (SpacetimeServer server in servers)
                Debug.Log($"Discovered server: {server}");
            
            // Setting will trigger the onIdentitySelectedDropdownChangedAsync event @ PublisherWindow
            for (int i = 0; i < servers.Count; i++)
            {
                SpacetimeServer server = servers[i];
                serverSelectedDropdown.choices.Add(server.Nickname);

                if (server.IsDefault)
                {
                    // Set the index to the most recently-added one
                    int recentlyAddedIndex = serverSelectedDropdown.choices.Count - 1;
                    serverSelectedDropdown.index = recentlyAddedIndex;
                }
            }
            
            // Ensure a default was found
            bool foundServer = servers.Count > 0;
            bool foundDefault = serverSelectedDropdown.index >= 0;
            if (foundServer && !foundDefault)
            {
                Debug.LogError("Found Servers, but no default: " +
                    $"Falling back to [0]:{servers[0].Nickname} and setting via CLI...");
                serverSelectedDropdown.index = 0;
            
                // We need a default server set
                string nickname = servers[0].Nickname;
                await SpacetimeDbPublisherCliActions.SetDefaultServerAsync(nickname);
            }

            // Process result -> Update UI
            await onEnsureServerDefaultSuccessAsync(autoProgressIdentities);
        }

        /// autoProgress to reveal Identities group on success?
        private async Task onEnsureServerDefaultSuccessAsync(bool autoProgressIdentities)
        {
            // Allow selection, show [+] server new reveal ui btn
            serverSelectedDropdown.pickingMode = PickingMode.Position;
            ShowUi(serverAddNewShowUiBtn);
            
            // Hide UI
            HideUi(serverConnectingStatusLabel);
            HideUi(serverNewGroupBox);
            
            // Show the next section, if !isLocalServerAndOffline
            _foundServer = true;

            if (!autoProgressIdentities)
            {
                return;
            }

            _ = await revealIdentitiesGroupIfNotOfflineLocalServerAsync();
        }

        /// <summary>autoProgress to reveal Publisher group on success?</summary>
        /// <returns>revealedIdentityFoldout</returns>
        private async Task<bool> revealIdentitiesGroupIfNotOfflineLocalServerAsync()
        {
            bool isLocalServerAndOffline = await pingLocalServerSetBtnsAsync();
            if (!isLocalServerAndOffline)
            {
                ShowUi(identityFoldout);
                return true; // revealedIdentityFoldout
            }

            return false; // !revealedIdentityFoldout
        }

        /// This will reveal the group and initially check for the spacetime cli tool
        private async Task revealPublisherGroupUiAsync()
        {
            // Show and enable group, but disable the publishAsync btn
            // to check/install Spacetime CLI tool
            clearLabels();
            publishGroupBox.SetEnabled(true);
            publishBtn.SetEnabled(false);
            setPublishReadyStatusIfOnline();
            ShowUi(publishStatusLabel);
            ShowUi(publishGroupBox);
            toggleDebugModeIfNotLocalhost(); // Always false if called from init
            ShowUi(publishFoldout);

            // If localhost, show start|stop server btns async on separate thread
            if (_foundServer)
            {
                bool isLocalServerAndOffline = await pingLocalServerSetBtnsAsync();
            }
            
            publishModuleNameTxt.Focus();
            publishModuleNameTxt.SelectNone();
        }

        /// <summary>
        /// 1. Shows or hide localhost btns if localhost
        /// 2. If localhost:
        ///     a. Pings the local server to see if it's online
        ///     b. Shows either Start|Stop local server btn
        ///     c. If offline, hide identities group + disable Publish btn
        /// </summary>
        /// <returns>isLocalServerAndOffline</returns>
        private async Task<bool> pingLocalServerSetBtnsAsync()
        {
            HideUi(publishLocalBtnsHoriz);
            
            bool isLocalServer = checkIsLocalhostServerSelected();
            if (isLocalServer)
            {
                ShowUi(publishLocalBtnsHoriz);
            }
            else
            {
                HideUi(publishLocalBtnsHoriz);
                return false; // !isLocalServerAndOffline
            }
            
            Debug.Log("Localhost server selected: Pinging for online status ...");
            setPingingLocalServerUi();
            
            // Run CLI cmd
            string serverName = serverSelectedDropdown.value;
            bool isOnline = await checkIsLocalServerOnlineAsync(serverName);
            Debug.Log($"Local server online? {isOnline}");

            onLocalServerOnlineOffline(isOnline);
            return !isOnline; // isLocalServerAndOffline
        }

        private void setPingingLocalServerUi()
        {
            publishStartLocalServerBtn.text = SpacetimeMeta.GetStyledStr(
                SpacetimeMeta.StringStyle.Action, "Connecting to local server ...");
            
            publishStartLocalServerBtn.SetEnabled(false);
            ShowUi(publishStartLocalServerBtn);
            HideUi(serverConnectingStatusLabel);
        }

        /// <returns>isOnline (successful ping) with short timeout</returns>
        private async Task<bool> checkIsLocalServerOnlineAsync(string serverName)
        {
            Assert.IsTrue(checkIsLocalhostServerSelected(), $"Expected {nameof(checkIsLocalhostServerSelected)}");

            // Run CLI command with short timeout
            PingServerResult pingResult = await SpacetimeDbCliActions.PingServerAsync(serverName);
            
            // Process result
            bool isSuccess = pingResult.IsServerOnline;
            if (isSuccess)
            {
                _lastServerPingSuccess = pingResult;
            }
            
            return isSuccess;
        }

        private void onLocalServerOnlineOffline(bool isOnline)
        {
            if (!isOnline)
            {
                setLocalServerOfflineUi(); // "Local server offline"
                return;
            }
            
            // Online
            clearLabels();
            HideUi(publishStartLocalServerBtn);
            ShowUi(publishStopLocalServerBtn);
                
            setStopLocalServerBtnTxt();
            setPublishReadyStatusIfOnline();
        }

        private void clearLabels()
        {
            publishStatusLabel.text = "";
            serverConnectingStatusLabel.text = "";
        }
        
        /// serverConnectingStatusLabel + publishStatusLabel
        private void setLocalServerOfflineUi()
        {
            const string serverOfflineMsg = "Local server offline";
            
            serverConnectingStatusLabel.text = SpacetimeMeta.GetStyledStr(
                SpacetimeMeta.StringStyle.Error, serverOfflineMsg);
            
            publishStatusLabel.text = SpacetimeMeta.GetStyledStr(
                SpacetimeMeta.StringStyle.Error, serverOfflineMsg);
            
            ShowUi(serverConnectingStatusLabel);
            ShowUi(publishStatusLabel);

            HideUi(publishStopLocalServerBtn);
            publishStartLocalServerBtn.text = "Start Local Server";
            publishStartLocalServerBtn.SetEnabled(true);
            ShowUi(publishStartLocalServerBtn);
            
            publishBtn.SetEnabled(false); // Just in case

            // Hide other groups
            toggleFoldoutRipple(FoldoutGroupType.Identity, show: false);
        }
        
        /// Sets status label to "Ready" and enables+shows Publisher btn
        /// +Hides the cancel btn
        private void setPublishReadyStatusIfOnline()
        {
            ShowUi(publishStatusLabel);

            if (_lastServerPingSuccess?.IsServerOnline == false)
            {
                setLocalServerOfflineUi();
            }
            else
            {
                // Make it look satisfying
                HideUi(publishStatusLabel);
                publishStatusLabel.text = SpacetimeMeta.GetStyledStr(
                    SpacetimeMeta.StringStyle.Success, "Ready");
                ShowUi(publishStatusLabel); // Fades in
            }
            
            publishBtn.SetEnabled(true);
            ShowUi(publishBtn);
            publishBtn.text = "Publish";
 
            HideUi(publishCancelBtn);
        }
        
        /// Be sure to try/catch this with a try/finally to dispose `_cts
        private async Task publishAsync()
        {
            setPublishStartUi();
            resetCancellationTokenSrc();

            bool enableDebugMode = publishModuleDebugModeToggle.enabledSelf && publishModuleClearDataToggle.value;
            
            PublishRequest publishRequest = new(
                publishModuleNameTxt.value, 
                publishModulePathTxt.value,
                new PublishRequest.AdvancedOpts(
                    publishModuleClearDataToggle.value,
                    enableDebugMode
                ));
            
            // Run CLI cmd [can cancel]
            PublishResult publishResult = await SpacetimeDbPublisherCliActions.PublishAsync(
                publishRequest,
                _publishCts.Token);

            // Process result -> Update UI
            bool isSuccess = publishResult.IsSuccessfulPublish;
            Debug.Log($"PublishAsync success: {isSuccess}");
            if (isSuccess)
            {
                onPublishSuccess(publishResult);
            }
            else
            {
                onPublishFail(publishResult);
            }
        }
        
        /// Critical err - show label
        private void onPublishFail(PublishResult publishResult)
        {
            _cachedPublishResult = null;

            if (publishResult.PublishErrCode == PublishResult.PublishErrorCode.Dotnet8PlusMissing)
            {
                // Launch installation URL + add to err
                publishResult.StyledFriendlyErrorMessage += ": Launching installation website. Install -> try again";
                Application.OpenURL("https://dotnet.microsoft.com/en-us/download/dotnet/8.0");
            }
            else if (publishResult.PublishErrCode == PublishResult.PublishErrorCode.MSB1003_InvalidProjectDir)
            {
                // Focus + select the server module path input
                publishModulePathTxt.Focus();
                publishModulePathTxt.SelectAll();
            }
            else if (publishResult.PublishErrCode == PublishResult.PublishErrorCode.DBUpdateRejected_PermissionDenied)
            {
                // Focus + select the server module name input
                publishModuleNameTxt.Focus();
                publishModuleNameTxt.SelectAll();
            } 
            
            updatePublishStatus(
                SpacetimeMeta.StringStyle.Error, 
                publishResult.StyledFriendlyErrorMessage 
                    ?? ClipString(publishResult.CliError, maxLength: 4000));
        }
        
        /// There may be a false-positive wasm-opt err here; in which case, we'd still run success.
        /// Caches the module name into EditorPrefs for other tools to use. 
        private void onPublishSuccess(PublishResult publishResult)
        {
            _cachedPublishResult = publishResult;
            
            // Success - reset UI back to normal
            setPublishReadyStatusIfOnline();
            setPublishResultGroupUi(publishResult);
            
            // Other editor tools may want to utilize this value,
            // since the CLI has no idea what you're "default" Module is
            EditorPrefs.SetString(
                SpacetimeMeta.EDITOR_PREFS_MODULE_NAME_KEY, 
                publishModuleNameTxt.value);
        }

        private void setPublishResultGroupUi(PublishResult publishResult)
        {
            // Hide old status -> Load the result data
            HideUi(publishResultStatusLabel);
            publishResultDateTimeTxt.value = $"{publishResult.PublishedAt:G} (Local)";
            publishResultHostTxt.value = publishResult.UploadedToHost;
            publishResultDbAddressTxt.value = publishResult.DatabaseAddressHash;
            
            // Set via ValueWithoutNotify since this is a hacky "readonly" Toggle (no official feat for this, yet)
            publishResultIsOptimizedBuildToggle.value = publishResult.IsPublishWasmOptimized;
            
            // Show install pkg button, to optionally optimize next publish
            if (publishResult.IsPublishWasmOptimized || publishResultIsOptimizedBuildToggle.value)
            {
                HideUi(installWasmOptBtn);
            }
            else
            {
                ShowUi(installWasmOptBtn);
            }

            resetGenerateUi();
            
            // Show the result group and expand the foldout
            revealPublishResultCacheIfHostExists(openFoldout: true);
        }

        /// Hide CLI group
        private void onSpacetimeCliAlreadyInstalled()
        {
            hideProgressBarAndCancel(installCliProgressBar);
            HideUi(installCliGroupBox);
        }

        /// Show a styled friendly string to UI. Errs will enable publishAsync btn.
        private void updatePublishStatus(SpacetimeMeta.StringStyle style, string friendlyStr)
        {
            publishStatusLabel.text = SpacetimeMeta.GetStyledStr(style, friendlyStr);
            ShowUi(publishStatusLabel);

            if (style != SpacetimeMeta.StringStyle.Error)
            {
                return; // Not an error
            }

            // Error: Hide cancel btn, cancel token, show/enable pub btn
            HideUi(publishCancelBtn);
            _publishCts?.Dispose();
            
            ShowUi(publishBtn);
            publishBtn.SetEnabled(true);
        }
        
        /// Yields 1 frame to update UI fast
        private void setPublishStartUi()
        {
            // Reset result cache
            resetPublishResultCache();
            
            // Hide: Publish btn, label, result foldout 
            HideUi(publishResultFoldout);
            FadeOutUi(publishStatusLabel);
            HideUi(publishBtn);
            
            // Show: Cancel btn, show progress bar,
            ShowUi(publishCancelBtn);
            
            _ = startProgressBarAsync(
                publishInstallProgressBar,
                barTitle: "Publishing to SpacetimeDB ...",
                autoHideOnComplete: false);
        }
        
        
        #region Install npm `wasm-opt` | Disabled until https://github.com/WebAssembly/binaryen fixes their Windows PATH detection
        // /// Set 'installing' UI
        // private void setinstallWasmOptPackageViaNpmUi()
        // {
        //     // Hide UI
        //     publishBtn.SetEnabled(false);
        //     installWasmOptBtn.SetEnabled(false);
        //     
        //     // Show UI
        //     installWasmOptBtn.text = SpacetimeMeta.GetStyledStr(
        //         SpacetimeMeta.StringStyle.Action, "Installing ...");
        //     ShowUi(installCliProgressBar);
        //     
        //     _ = startProgressBarAsync(
        //         installWasmOptProgressBar,
        //         barTitle: "Installing `wasm-opt` via npm ...",
        //         autoHideOnComplete: false);
        // }
        //
        // /// Install `wasm-opt` npm pkg for a "set and forget" publishAsync optimization boost
        // /// BUG: (!) `wasm-opt` will show up in PATH, but not recognized by the publish util
        // private async Task installWasmOptPackageViaNpmAsync()
        // {
        //     setinstallWasmOptPackageViaNpmUi();
        //     
        //     // Run CLI cmd
        //     InstallWasmResult installWasmResult = await SpacetimeDbPublisherCliActions.InstallWasmOptPkgAsync();
        //
        //     // Process result -> Update UI
        //     bool isSuccess = installWasmResult.IsSuccessfulInstall;
        //     onInstallWasmOptPackageViaNpmDone();
        //     if (isSuccess)
        //     {
        //         onInstallWasmOptPackageViaNpmSuccess();
        //     }
        //     else
        //     {
        //         onInstallWasmOptPackageViaNpmFail(installWasmResult);
        //     }
        // }
        //
        // private void onInstallWasmOptPackageViaNpmDone()
        // {
        //     hideProgressBarAndCancel(installWasmOptProgressBar);
        //     publishBtn.SetEnabled(true);
        // }
        //
        // /// Success: Show installed txt, keep button disabled, but don't actually check
        // /// the optimization box since *this* publishAsync is not optimized: Next one will be
        // private void onInstallWasmOptPackageViaNpmSuccess()
        // {
        //     installWasmOptBtn.text = SpacetimeMeta.GetStyledStr(
        //         SpacetimeMeta.StringStyle.Success, "Installed");
        // }
        //
        // private void onInstallWasmOptPackageViaNpmFail(InstallWasmResult installResult)
        // {
        //     installWasmOptBtn.SetEnabled(true);
        //     
        //     // Caught err?
        //     string friendlyErrDetails = "wasm-opt install failed";
        //     if (installResult.InstallWasmError == InstallWasmResult.InstallWasmErrorType.NpmNotRecognized)
        //     {
        //         friendlyErrDetails = "Missing `npm`";
        //     }
        //
        //     installWasmOptBtn.text = SpacetimeMeta.GetStyledStr(
        //         SpacetimeMeta.StringStyle.Error, friendlyErrDetails);
        // }
        #endregion // Install npm `wasm-opt` | Disabled until https://github.com/WebAssembly/binaryen fixes their Windows PATH detection
        

        /// UI: Disable btn + show installing status to id label
        private void setAddIdentityUi(string nickname)
        {
            identityAddBtn.SetEnabled(false);
            identityStatusLabel.text = SpacetimeMeta.GetStyledStr(
                SpacetimeMeta.StringStyle.Action, $"Adding {nickname} ...");
            ShowUi(identityStatusLabel);
            FadeOutUi(publishStatusLabel);
            HideUi(publishResultFoldout);
        }
        
        /// autoProgress to reveal Publisher group on success?
        private async Task addIdentityAsync(
            string nickname,
            string email,
            bool autoProgressPublisher)
        {
            // Sanity check
            if (string.IsNullOrEmpty(nickname) || string.IsNullOrEmpty(email))
            {
                return;
            }

            setAddIdentityUi(nickname);
            AddIdentityRequest addIdentityRequestRequest = new(nickname, email);
            
            // Run CLI cmd
            AddIdentityResult addIdentityResult = await SpacetimeDbPublisherCliActions.AddIdentityAsync(addIdentityRequestRequest);
            SpacetimeIdentity identity = new(nickname, isDefault:true);

            // Process result -> Update UI
            if (addIdentityResult.HasCliErr)
            {
                onAddIdentityFail(identity, addIdentityResult);
            }
            else
            {
                onAddIdentitySuccess(identity, autoProgressPublisher);
            }
        }
        
        /// Success: Add to dropdown + set default + show. Hide the [+] add group.
        /// Don't worry about caching choices; we'll get the new choices via CLI each load
        /// autoProgress to reveal Publisher group on success?
        private async void onAddIdentitySuccess(SpacetimeIdentity identity, bool autoProgressPublisher)
        {
            Debug.Log($"Add new identity success: {identity.Nickname}");
            resetPublishResultCache();
            
            List<SpacetimeIdentity> identities = new() { identity };
            await populateIdentitiesDropdownEnsureDefaultAsync(identities, autoProgressPublisher);
        }
        
        private void onAddIdentityFail(SpacetimeIdentity identity, AddIdentityResult addIdentityResult)
        {
            identityAddBtn.SetEnabled(true);
            identityStatusLabel.text = SpacetimeMeta.GetStyledStr(
                SpacetimeMeta.StringStyle.Error, 
                $"<b>Failed:</b> Couldn't add identity `{identity.Nickname}`\n" +
                addIdentityResult.StyledFriendlyErrorMessage);

            if (addIdentityResult.AddIdentityError == AddIdentityResult.AddIdentityErrorType.IdentityAlreadyExists)
            {
                identityNicknameTxt.Focus();
                identityNicknameTxt.SelectAll();
            }
            
            ShowUi(identityStatusLabel);
        }

        private void setAddServerUi(string nickname)
        {
            // UI: Disable btn + show installing status to id label
            serverAddBtn.SetEnabled(false);
            serverConnectingStatusLabel.text = SpacetimeMeta.GetStyledStr(
                SpacetimeMeta.StringStyle.Action, $"Adding {nickname} ...");
            ShowUi(serverConnectingStatusLabel);
            
            // Hide the other sections (while clearing out their labels), since we rely on servers
            HideUi(identityStatusLabel);
            HideUi(identityFoldout);
            HideUi(publishFoldout);
            FadeOutUi(publishStatusLabel);
            HideUi(publishResultFoldout);
        }
        
        /// autoProgress Identities on success?
        private async Task addServerAsync(
            string nickname,
            string host,
            bool autoProgressIdentities)
        {
            // Sanity check
            if (string.IsNullOrEmpty(nickname) || string.IsNullOrEmpty(host))
            {
                return;
            }

            setAddServerUi(nickname);
            AddServerRequest request = new(nickname, host);

            // Run the CLI cmd
            AddServerResult addServerResult = await SpacetimeDbPublisherCliActions.AddServerAsync(request);
            
            // Process result -> Update UI
            SpacetimeServer serverAdded = new(nickname, host, isDefault:true);

            if (addServerResult.HasCliErr)
            {
                onAddServerFail(serverAdded, addServerResult);
            }
            else
            {
                onAddServerSuccess(serverAdded, autoProgressIdentities);
            }
        }
        
        private void onAddServerFail(SpacetimeServer serverAdded, AddServerResult addServerResult)
        {
            serverAddBtn.SetEnabled(true);
            serverConnectingStatusLabel.text = SpacetimeMeta.GetStyledStr(SpacetimeMeta.StringStyle.Error, 
                $"<b>Failed:</b> Couldn't add `{serverAdded.Nickname}` server</b>\n" +
                addServerResult.StyledFriendlyErrorMessage);
                
            ShowUi(serverConnectingStatusLabel);
        }
        
        /// Success: Add to dropdown + set default + show. Hide the [+] add group.
        /// Don't worry about caching choices; we'll get the new choices via CLI each load
        /// autoProgress to reveal Identities group on success?
        private void onAddServerSuccess(SpacetimeServer server, bool autoProgressIdentities)
        {
            Debug.Log($"Add new server success: {server.Nickname}");
            resetPublishResultCache();

            List<SpacetimeServer> serverList = new() { server };
            _ = onGetSetServersSuccessEnsureDefaultAsync(serverList, autoProgressIdentities);
        }

        private async Task setDefaultIdentityAsync(string idNicknameOrDbAddress)
        {
            // Sanity check
            if (string.IsNullOrEmpty(idNicknameOrDbAddress))
            {
                return;
            }

            // Run CLI cmd
            SpacetimeCliResult cliResult = await SpacetimeDbPublisherCliActions.SetDefaultIdentityAsync(idNicknameOrDbAddress);

            // Process result -> Update UI
            bool isSuccess = !cliResult.HasCliErr;
            if (!isSuccess)
            {
                Debug.LogError($"Failed to {nameof(setDefaultIdentityAsync)}: {cliResult.CliError}");
                return;
            }
            
            Debug.Log($"Changed default identity to: {idNicknameOrDbAddress}");
            identityAddNewShowUiBtn.text = "+";
        }

        private void resetPublishResultCache()
        {
            publishResultFoldout.value = false;
            publishResultDateTimeTxt.value = "";
            publishResultHostTxt.value = "";
            publishResultDbAddressTxt.value = "";
            
            publishResultIsOptimizedBuildToggle.value = false;
            ShowUi(installWasmOptBtn);
            HideUi(installWasmOptProgressBar);
            
            HideUi(publishResultStatusLabel);
            
            publishResultGenerateClientFilesBtn.SetEnabled(true);
            publishResultGenerateClientFilesBtn.text = "Generate Client Typings";
            
            // Hacky readonly Toggle feat workaround
            publishResultIsOptimizedBuildToggle.SetEnabled(false);
            publishResultIsOptimizedBuildToggle.style.opacity = 1;
        }

        private void resetGetServerLogsUi()
        {
            publishResultGetServerLogsBtn.SetEnabled(true);
            publishResultGetServerLogsBtn.text = "Server Logs";
        }
        
        /// Toggles the group visibility of the foldouts. Labels also hide if !show.
        /// Toggles ripple downwards from top. Checks for nulls
        private void toggleFoldoutRipple(FoldoutGroupType startRippleFrom, bool show)
        {
            // ---------------
            // Server, Identity, Publish, PublishResult
            if (startRippleFrom <= FoldoutGroupType.Server)
            {
                if (show)
                {
                    ShowUi(serverFoldout);
                }
                else
                {
                    HideUi(serverConnectingStatusLabel);
                    HideUi(serverFoldout);
                }
            }
            
            // ---------------
            // Identity, Publish, PublishResult
            if (startRippleFrom <= FoldoutGroupType.Identity)
            {
                if (show)
                {
                   ShowUi(identityFoldout); 
                }
                else
                {
                    HideUi(identityFoldout); 
                    HideUi(identityStatusLabel);
                }
            }
            else
            {
                return;
            }

            // ---------------
            // Publish, PublishResult
            if (startRippleFrom <= FoldoutGroupType.Publish)
            {
                HideUi(publishFoldout);
                if (!show)
                {
                    FadeOutUi(publishStatusLabel);
                }
            }
            else
            {
                return;
            }

            // ---------------
            // PublishResult+
            if (startRippleFrom <= FoldoutGroupType.PublishResult)
            {
                HideUi(publishResultFoldout);
            }
        }
        
        /// UI: This invalidates identities, so we'll hide all Foldouts
        /// If local, we'll need extra time to ping (show status)
        private void setDefaultServerRefreshIdentitiesUi()
        {
            toggleFoldoutRipple(FoldoutGroupType.Identity, show: false);
            toggleSelectedServerProcessingEnabled(setEnabled: false);
            
            serverConnectingStatusLabel.text = SpacetimeMeta.GetStyledStr(
                SpacetimeMeta.StringStyle.Action, "Connecting ...");
            ShowUi(serverConnectingStatusLabel);
        }

        /// Change to a *known* nicknameOrHost
        /// - Changes CLI default server
        /// - Revalidates identities, since they are bound per-server
        /// - autoProgress to reveal Publish group on success?
        private async Task setDefaultServerRefreshIdentitiesAsync(
            string nicknameOrHost, 
            bool autoProgressPublish)
        {
            // Sanity check
            if (string.IsNullOrEmpty(nicknameOrHost))
            {
                return;
            }
            
            setDefaultServerRefreshIdentitiesUi(); // Hide all foldouts [..]

            // Run CLI cmd
            SpacetimeCliResult cliResult = await SpacetimeDbPublisherCliActions.SetDefaultServerAsync(nicknameOrHost);
            
            // Process result -> Update UI
            bool isSuccess = !cliResult.HasCliErr;
            if (!isSuccess)
            {
                onChangeDefaultServerFail(cliResult);
            }
            else
            {
                await onChangeDefaultServerSuccessAsync(autoProgressPublish);
            }
            
            toggleSelectedServerProcessingEnabled(setEnabled: true);
        }

        /// Enables or disables the selected server dropdown + add new btn
        private void toggleSelectedServerProcessingEnabled(bool setEnabled)
        {
            serverSelectedDropdown.SetEnabled(setEnabled);
            serverAddNewShowUiBtn.SetEnabled(setEnabled);
        }
        
        private void onChangeDefaultServerFail(SpacetimeCliResult cliResult)
        {
            serverSelectedDropdown.SetEnabled(true);

            string clippedCliErr = ClipString(cliResult.CliError, maxLength: 4000);
            serverConnectingStatusLabel.text = SpacetimeMeta.GetStyledStr(
                SpacetimeMeta.StringStyle.Error,
                $"<b>Failed to Change Servers:</b>\n{clippedCliErr}");
            ShowUi(serverConnectingStatusLabel);
        }
        
        /// Invalidate identities
        /// autoProgress to reveal Identities group on success?
        private async Task onChangeDefaultServerSuccessAsync(bool autoProgressIdentities)
        {
            bool isLocalServerAndOffline = await pingLocalServerSetBtnsAsync();
            
            // UI: Hide label fast so it doesn't look laggy
            if (!isLocalServerAndOffline)
            {
                // Don't hide: It should say server offline
                HideUi(serverConnectingStatusLabel);
            }
            
            serverSelectedDropdown.SetEnabled(true);
            serverAddNewShowUiBtn.text = "+";
            resetPublishResultCache(); // We don't want stale info from a different server's publish showing
            
            // Process and reveal the next UI group
            if (!autoProgressIdentities)
            {
                return;
            }

            // (!) Don't reveal the identities group if we selected a local server and it's offline
            // Any Identity interactions (or anything after this) requires a live server
            if (!isLocalServerAndOffline)
            {
                await getIdentitiesSetDropdown(autoProgressPublisher: true);
            }
        }

        /// Disable generate btn, show "GGenerating..." label
        private void setGenerateClientFilesUi()
        {
            HideUi(publishResultStatusLabel);
            publishResultGenerateClientFilesBtn.SetEnabled(false);
            publishResultGenerateClientFilesBtn.text = SpacetimeMeta.GetStyledStr(
                SpacetimeMeta.StringStyle.Action,
                "Generating ...");
        }

        private async Task generateClientFilesAsync()
        {
            setGenerateClientFilesUi();
            
            // Prioritize result cache, if any - else use the input field
            string serverModulePath = _cachedPublishResult?.Request?.ServerModulePath 
                ?? publishModulePathTxt.value;
            
            Assert.IsTrue(!string.IsNullOrEmpty(serverModulePath),
                $"Expected {nameof(serverModulePath)}");

            if (generatedFilesExist())
            {
                // Wipe old files
                Directory.Delete(PathToAutogenDir, recursive:true);
            }
            
            GenerateRequest request = new(
                serverModulePath,
                PathToAutogenDir,
                deleteOutdatedFiles: true);

            GenerateResult generateResult = await SpacetimeDbPublisherCliActions
                .GenerateClientFilesAsync(request);

            bool isSuccess = generateResult.IsSuccessfulGenerate;
            if (isSuccess)
            {
                onGenerateClientFilesSuccess(serverModulePath);
            }
            else
            {
                onGenerateClientFilesFail(generateResult);
            }
        }

        /// Disable get logs btn, show action text
        private void setGetServerLogsAsyncUi()
        {
            publishResultGetServerLogsBtn.SetEnabled(false);
            publishResultGetServerLogsBtn.text = SpacetimeMeta.GetStyledStr(
                SpacetimeMeta.StringStyle.Action, "Fetching ...");
        }
        
        /// Gets server logs of selected server name
        private async Task getServerLogsAsync()
        {
            setGetServerLogsAsyncUi();

            string serverName = publishModuleNameTxt.text;
            SpacetimeCliResult cliResult = await SpacetimeDbCliActions.GetLogsAsync(serverName);
        
            resetGetServerLogsUi();
            if (cliResult.HasCliErr)
            {
                Debug.LogError($"Failed to {nameof(getServerLogsAsync)}: {cliResult.CliError}");
                return;
            }

            onGetServerLogsSuccess(cliResult);
        }

        /// Output logs to console, with some basic style
        private void onGetServerLogsSuccess(SpacetimeCliResult cliResult)
        {
            string infoColor = SpacetimeMeta.INPUT_TEXT_COLOR;
            string warnColor = SpacetimeMeta.ACTION_COLOR_HEX;
            string errColor = SpacetimeMeta.ERROR_COLOR_HEX;
            
            // Just color the log types for easier reading
            string styledLogs = cliResult.CliOutput
                .Replace("INFO:", $"<color={infoColor}><b>INFO:</b></color>")
                .Replace("WARNING:", SpacetimeMeta.GetStyledStr(SpacetimeMeta.StringStyle.Action, "<b>WARNING:</b>"))
                .Replace("ERROR:", SpacetimeMeta.GetStyledStr(SpacetimeMeta.StringStyle.Action, "<b>ERROR:</b>"));

            Debug.Log($"<color={SpacetimeMeta.ACTION_COLOR_HEX}><b>Formatted Server Logs:</b></color>\n" +
                $"```bash\n{styledLogs}\n```");
        }

        private void onGenerateClientFilesFail(SpacetimeCliResult cliResult)
        {
            Debug.LogError($"Failed to generate client files: {cliResult.CliError}");

            resetGenerateUi();
            
            string clippedCliErr = ClipString(cliResult.CliError, maxLength: 4000);
            publishResultStatusLabel.text = SpacetimeMeta.GetStyledStr(
                SpacetimeMeta.StringStyle.Error,
                $"<b>Failed to Generate:</b>\n{clippedCliErr}");
            
            ShowUi(publishResultStatusLabel);
        }

        private void onGenerateClientFilesSuccess(string serverModulePath)
        {
            Debug.Log($"Generated SpacetimeDB client files from:" +
                $"\n`{serverModulePath}`\n\nto:\n`{PathToAutogenDir}`");
         
            resetGenerateUi();
            publishResultStatusLabel.text = SpacetimeMeta.GetStyledStr(
                SpacetimeMeta.StringStyle.Success,
                "Generated to dir: <color=white>Assets/Autogen/</color>");
            ShowUi(publishResultStatusLabel);
        }
        
        bool generatedFilesExist() => Directory.Exists(PathToAutogenDir);

        /// Shared Ui changes after success/fail, or init on ui reset
        private void resetGenerateUi()
        {
            publishResultGenerateClientFilesBtn.text = generatedFilesExist()
                ? "Regenerate Client Typings"
                : "Generate Client Typings";
            
            HideUi(publishResultStatusLabel);
            publishResultGenerateClientFilesBtn.SetEnabled(true);
        }

        /// Assuming !https
        private bool checkIsLocalhostServerSelected() =>
            serverSelectedDropdown.value.StartsWith(SpacetimeMeta.LOCAL_SERVER_NAME);

        private void setStartingLocalServerUi()
        {
            FadeOutUi(serverConnectingStatusLabel);
            FadeOutUi(publishStatusLabel);
            publishStartLocalServerBtn.SetEnabled(false);
            publishStartLocalServerBtn.text = SpacetimeMeta.GetStyledStr(
                SpacetimeMeta.StringStyle.Action, "Starting ...");
            FadeOutUi(publishStatusLabel);
        }
        
        /// <summary>Starts the local SpacetimeDB server; sets _localServer state.</summary>
        /// <returns>startedServer</returns>
        private async Task<bool> startLocalServer()
        {
            setStartingLocalServerUi();
            
            // Run async CLI cmd => wait for connection => Save to state cache
            string serverName = serverSelectedDropdown.value;
            PingServerResult pingResult = await SpacetimeDbCliActions
                .StartDetachedLocalServerWaitUntilOnlineAsync(serverName);
            
            // Process result -> Update UI
            if (!pingResult.IsServerOnline)
            {
                // Offline
                onStartLocalServerFail(pingResult);
                return false; // !startedServer 
            }
            
            // Online
            await onStartLocalServerSuccessAsync(pingResult);
            return true; // startedServer
        }

        private async Task onStartLocalServerSuccessAsync(PingServerResult pingResult)
        {
            Debug.Log($"Started local server @ `{_lastServerPingSuccess}`");
            _lastServerPingSuccess = pingResult;

            HideUi(serverConnectingStatusLabel);
            HideUi(publishStartLocalServerBtn);
            
            // The server is now running: Show the button to stop it (with a slight delay to enable)
            setStopLocalServerBtnTxt();
            ShowUi(publishStopLocalServerBtn);
            publishStopLocalServerBtn.SetEnabled(false);
            _ = WaitEnableElementAsync(publishStopLocalServerBtn, TimeSpan.FromSeconds(1));
            
            // setPublishReadyStatusIfOnline();
            await getIdentitiesSetDropdown(autoProgressPublisher: true);
        }

        /// Sets stop server btn to "Stop {server}@{hostUrlWithoutHttp}"
        /// Pulls host from _lastServerPinged
        private void setStopLocalServerBtnTxt()
        {
            if (string.IsNullOrEmpty(_lastServerPingSuccess?.HostUrl))
            {
                // Fallback
                publishStopLocalServerBtn.text = "Stop Local Server";
                return;
            }
            
            string host = _lastServerPingSuccess.HostUrl.Replace("127.0.0.1", "localhost");
            publishStopLocalServerBtn.text = SpacetimeMeta.GetStyledStr(
                SpacetimeMeta.StringStyle.Error, $"Stop {serverSelectedDropdown.value}@{host}");
        }

        /// The last ping was cached to _lastServerPinged
        /// <param name="pingResult"></param>
        private void onStartLocalServerFail(PingServerResult pingResult)
        {
            Debug.LogError($"Failed to {nameof(startLocalServer)}");

            publishStartLocalServerBtn.text = "Start Local Server";
            publishStartLocalServerBtn.SetEnabled(true);
        }

        /// <returns>stoppedServer</returns>
        private async Task<bool> stopLocalServer()
        {
            if (_lastServerPingSuccess.Port == 0)
            {
                string serverName = serverSelectedDropdown.value;
                PingServerResult pingResult = await SpacetimeDbCliActions.PingServerAsync(serverName);
                if (pingResult.IsServerOnline)
                    _lastServerPingSuccess = pingResult;
            }
            
            // Validate + Logs + UI
            Debug.Log($"Attempting to force stop local server running on port:{_lastKnownPort}");
            setStoppingLocalServerUi();
            
            // Run CLI cmd => Save to state cache
            SpacetimeCliResult cliResult = await SpacetimeDbCliActions.ForceStopLocalServerAsync(_lastKnownPort);
            
            // Process result -> Update UI
            bool isSuccess = !cliResult.HasCliErr;
            if (!isSuccess)
            {
                Debug.LogError($"Failed to {nameof(stopLocalServer)}: {cliResult.CliError}");
                throw new Exception("TODO: Handle a rare CLI error on stop server fail");
                return false; // !stoppedServer 
            }

            onStopLocalServerSuccess();
            return true; // stoppedServer
        }

        private void setStoppingLocalServerUi()
        {
            publishStopLocalServerBtn.SetEnabled(false);
            publishStopLocalServerBtn.text = SpacetimeMeta.GetStyledStr(
                SpacetimeMeta.StringStyle.Action, "Stopping ...");
            FadeOutUi(publishStatusLabel);
        }

        /// We stopped -> So now we want to show start (+disable publish)
        private void onStopLocalServerSuccess()
        {
            Debug.Log(SpacetimeMeta.GetStyledStr(SpacetimeMeta.StringStyle.Error, "Stopped local server"));
            
            HideUi(publishStopLocalServerBtn);
            
            publishStartLocalServerBtn.text = "Start Local Server";
            publishStartLocalServerBtn.SetEnabled(true);
            ShowUi(publishStartLocalServerBtn);
            
            setLocalServerOfflineUi();
            publishBtn.SetEnabled(false);
            
            // We should only show the Servers dropdown since we can't do anything with an offline local server
            toggleFoldoutRipple(FoldoutGroupType.Identity, show: false);
        }
    }
}
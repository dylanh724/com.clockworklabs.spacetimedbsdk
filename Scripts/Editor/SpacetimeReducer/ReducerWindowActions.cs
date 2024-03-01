using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace SpacetimeDB.Editor
{
    /// Unlike ReducerWindowCallbacks, these are not called *directly* from UI.
    /// Runs an action -> Processes isSuccess -> calls success || fail @ ReducerWindowCallbacks.
    /// ReducerWindowCallbacks should handle try/catch (except for init chains).
    public partial class ReducerWindow
    {
        #region Init from ReducerWindow.CreateGUI
        /// Gets selected server + identity. On err, refers to PublisherWindow
        /// Initially called by ReducerWindow @ CreateGUI.
        private async Task initDynamicEventsFromReducerWindow()
        {
            Debug.Log("initDynamicEventsFromReducerWindow");
            
            await ensureCliInstalledAsync();
            await setSelectedServerTxtAsync();
            await setSelectedIdentityTxtAsync();
            
            //// TODO: If `spacetime list` ever returns db names (not just addresses),
            //// TODO: Auto list them in dropdown
            setSelectedModuleTxtAsync();
            
            // At this point, sanity  check an existing module name to continue
            if (string.IsNullOrEmpty(moduleNameTxt.value))
            {
                return;
            }
            
            await setReducersTreeViewAsync();

            // Load entities into TreeView
            throw new NotImplementedException("TODO: Load entities into TreeView");

            // Show Actions foldout
            throw new NotImplementedException("TODO: Show Actions foldout");
        }

        /// Pulls from publisher, if any
        private void setSelectedModuleTxtAsync()
        {
            // Other editor tools may want to utilize this value,
            // since the CLI has no idea what you're "default" Module is
            moduleNameTxt.value = EditorPrefs.GetString(
                SpacetimeMeta.EDITOR_PREFS_MODULE_NAME_KEY, 
                defaultValue: "");
        }

        /// Loads reducer names into #reducersTreeView -> Enable
        /// Doc | https://docs.unity3d.com/2022.3/Documentation/Manual/UIE-uxml-element-TreeView.html
        private async Task setReducersTreeViewAsync()
        {
            string moduleName = moduleNameTxt.value;
            GetEntityStructureResult entityStructureResult = await SpacetimeDbCli.GetEntityStructure(moduleName);
            
            bool isSuccess = entityStructureResult is { HasEntityStructure: true };
            if (!isSuccess)
            { 
                Debug.Log("Warning: Searched for reducers; found none");
                return;
            }
            
            // Success: Load entity names into reducer tree view - cache _entityStructure state
            // TODO: +with friendly styled syntax hint children
            _entityStructure = entityStructureResult.EntityStructure;

            reducersTreeView.Clear();
            List<TreeViewItemData<string>> treeViewItems = new();

            for (int i = 0; i < _entityStructure.ReducersInfo.Count; i++)
            {
                ReducerInfo reducerInfo = _entityStructure.ReducersInfo[i];
                
                // TODO: Subitems, eg: treeViewSubItemsData.Add(new TreeViewItemData<string>(subItem.Id, subItem.Name));
                List<TreeViewItemData<string>> treeViewSubItemsData = new(); // Children

                TreeViewItemData<string> treeViewItemData = new(
                    id: i,
                    reducerInfo.GetReducerName(),
                    treeViewSubItemsData);

                treeViewItems.Add(treeViewItemData);
            }

            reducersTreeView.SetRootItems(treeViewItems);
            reducersTreeView.Rebuild();

            // Enable the TreeView, hide loading status
            reducersTreeView.SetEnabled(true);
            reducersLoadingLabel.style.display = DisplayStyle.None;
        }

        /// Show the actions foldout + syntax hint + focus the arg input, if any
        private void setAction(int index)
        {
            int argsCount = _entityStructure.ReducersInfo[index].ReducerEntity.Arity;
            List<string> styledSyntaxHints = _entityStructure.ReducersInfo[index].GetNormalizedStyledSyntaxHints();

            if (argsCount > 0)
            {
                // Set txt + txt label -> enable
                actionTxt.value = "";
                actionTxt.style.display = DisplayStyle.Flex;
                actionTxt.SetEnabled(true);
                
                // Set syntax hint label -> show
                actionsSyntaxHintLabel.text = string.Join("  ", styledSyntaxHints);
                actionsSyntaxHintLabel.style.display = DisplayStyle.Flex;    
            }
            else
            {
                // Disable txt, set label to sanity check no args
                actionTxt.SetEnabled(false);
                actionsSyntaxHintLabel.text = ""; // Just empty so we don't shift the UI
            }
            
            actionsFoldout.style.display = DisplayStyle.Flex;
            actionTxt.Focus(); // UX
        }


        

        /// We only expect a single index changed
        /// (!) Looking for name? See onReducerTreeViewSelectionChanged()
        private void onReducerTreeViewIndicesChanged(IEnumerable<int> selectedIndices)
        {
            // Get selected index, or fallback to -1
            int selectedIndex = selectedIndices != null && selectedIndices.Any() 
                ? selectedIndices.First()
                : -1;

            if (selectedIndex == -1)
            {
                // User pressed ESC
                actionsFoldout.style.display = DisplayStyle.None;
                return;
            }

            // Since we have a real selection, show the actions foldout + syntax hint + focus the arg input, if any
            setAction(selectedIndex);
        }
        
        /// We only expect a single element changed
        /// (!) Looking for index? See onReducerTreeViewIndicesChanged()
        private void onReducerTreeViewSelectionChanged(IEnumerable<object> obj)
        {
            // The first element should be the string name of the element Label.
            // Fallback to null if obj count is null or 0
            bool isNullOrEmpty = obj == null || !obj.Any();

            if (isNullOrEmpty)
            {
                actionsRunBtn.SetEnabled(false);
                return;
            }

            // We have a new selection - when we run, we'll use this name
            // string selectedReducerName = obj.First().ToString();
            enableActionRunBtnIfAriaOk();
        }

        /// 0 args? Enable! Else, ensure some input
        private void enableActionRunBtnIfAriaOk()
        {
            int argsCount = _entityStructure.ReducersInfo[reducersTreeView.selectedIndex].ReducerEntity.Arity;
            if (argsCount == 0)
            {
                // No args: Enable right away
                actionsRunBtn.SetEnabled(true);
                return;
            }

            // Ensure some input
            bool hasInput = !string.IsNullOrWhiteSpace(actionTxt.value);
            actionsRunBtn.SetEnabled(hasInput);
            
            // TODO: Cache a map of reducer to arg field to persist the previous test
        }

        private string getSelectedReducerName() => reducersTreeView.selectedItem as string;
        private async Task setSelectedServerTxtAsync() 
        {
            GetServersResult getServersResult = await SpacetimeDbCli.GetServersAsync();
            
            bool isSuccess = getServersResult.HasServer && !getServersResult.HasServersButNoDefault;
            if (!isSuccess)
            {
                showErrorWrapper("<b>Failed to get servers:</b>\n" +
                    "Setup via top menu `Window/SpacetimeDB/Publisher`");
                return;
            }
            
            // Success
            SpacetimeServer defaultServer = getServersResult.Servers
                .First(server => server.IsDefault);
            serverNameTxt.value = defaultServer.Nickname;
        }

        /// Load selected identities => set readonly identity txt
        private async Task setSelectedIdentityTxtAsync()
        {
            GetIdentitiesResult getIdentitiesResult = await SpacetimeDbCli.GetIdentitiesAsync();

            bool isSuccess = getIdentitiesResult.HasIdentity && !getIdentitiesResult.HasIdentitiesButNoDefault;
            if (!isSuccess)
            {
                showErrorWrapper("<b>Failed to get identities:</b>\n" +
                    "Setup via top menu `Window/SpacetimeDB/Publisher`");
                return;
            }

            // Success
            SpacetimeIdentity defaultIdentity = getIdentitiesResult.Identities
                .First(id => id.IsDefault);
            identityNameTxt.value = defaultIdentity.Nickname;
        }

        private async Task ensureCliInstalledAsync()
        {
            // Ensure CLI installed -> Show err (refer to PublisherWindow), if not
            SpacetimeCliResult isSpacetimeDbCliInstalledResult = await SpacetimeDbCli.GetIsSpacetimeCliInstalledAsync();

            bool isCliInstalled = !isSpacetimeDbCliInstalledResult.HasCliErr;
            if (!isCliInstalled)
            {
                showErrorWrapper("<b>SpacetimeDB CLI is not installed:</b>\n" +
                    "Setup via top menu `Window/SpacetimeDB/Publisher`");
                return;
            }
            
            // Success: Do nothing!
        }

        /// Initially called by ReducerWindow @ CreateGUI
        /// - Set to the initial state as if no inputs were set.
        /// - This exists so we can show all ui elements simultaneously in the
        ///   ui builder for convenience.
        /// - (!) If called from CreateGUI, after a couple frames,
        ///       any persistence from `ViewDataKey`s may override this.
        private void resetUi()
        {
            serverNameTxt.value = "";
            identityNameTxt.value = "";
            
            reducersTreeView.Clear();
            reducersTreeView.SetEnabled(false);
            
            resetActionsFoldoutUi();
        }

        private void resetActionsFoldoutUi()
        {
            actionsFoldout.style.display = DisplayStyle.None;
            actionsSyntaxHintLabel.style.display = DisplayStyle.None;
            actionsRunBtn.SetEnabled(false);
        }
        #endregion // Init from ReducerWindow.CreateGUI


        /// Wraps the entire body in an error message, generally when there's
        /// a cli/server/identity error that should be configured @ PublisherWindow (not here).
        /// Wraps text in error style color.
        /// Throws.
        private void showErrorWrapper(string friendlyError)
        {
            throw new NotImplementedException($"TODO: Hide body -> show err: {friendlyError}");
        }
    }
}
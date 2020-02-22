using System.IO;
using UnityEditor;
using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using System.Linq;

namespace DevLocker.Tools.AssetsManagement
{
	/// <summary>
	/// Window with a list of all available scenes in the project:
	/// + quick access to scenes
	/// + easily load scenes additively
	/// + pin favourites
	///
	/// Initial version of the script: http://wiki.unity3d.com/index.php/SceneViewWindow by Kevin Tarchenski.
	/// Advanced (this) version by Filip Slavov (a.k.a. NibbleByte) - NibbleByte3@gmail.com.
	/// </summary>
	public class ScenesInProject : EditorWindow
	{
		[MenuItem("Tools/Assets Management/Scenes In Project")]
		private static void Init()
		{
			var window = (ScenesInProject)GetWindow(typeof(ScenesInProject), false, "Scenes In Project");
			window.position = new Rect(window.position.xMin + 100f, window.position.yMin + 100f, 300f, 400f);
		}

		#region Types definitions

		private enum PinnedOptions
		{
			Unpin,
			MoveUp,
			MoveDown,
			MoveFirst,
			MoveLast,
			ShowInExplorer,
			ShowInProject,
		}

		private enum SortType
		{
			MostRecent,
			ByFileName,
			ByPath,
		}

		private enum SceneDisplay
		{
			SceneNames,
			ScenePaths
		}

		[Serializable]
		private class SceneEntry
		{
			public SceneEntry() { }
			public SceneEntry(string path)
			{
				Path = path;
				Name = System.IO.Path.GetFileNameWithoutExtension(Path);
				Folder = System.IO.Path.GetDirectoryName(Path);
			}

			public string Path;
			public string Name;
			public string Folder;
			public bool FirstInGroup = false;

			public override string ToString()
			{
				return Path;
			}
		}

		[Serializable]
		private class PersonalPreferences
		{
			public SortType SortType = SortType.MostRecent;
			public SceneDisplay SceneDisplay = SceneDisplay.SceneNames;
			public int ListCountLimit = 64;     // Round number... :D
			public int SpaceBetweenGroups = 6;  // Pixels between scenes with different folders.
			public int SpaceBetweenGroupsPinned = 0;  // Same but for pinned.

			public PersonalPreferences Clone()
			{
				return (PersonalPreferences)MemberwiseClone();
			}
		}

		#endregion

		private readonly char[] FILTER_WORD_SEPARATORS = new char[] { ' ', '\t' };
		private GUIStyle LEFT_ALIGNED_BUTTON;
		private GUIContent m_PreferencesButtonTextCache = new GUIContent("P", "Preferences...");
		private GUIContent m_SceneButtonTextCache = new GUIContent();
		private GUIContent m_AddSceneButtonTextCache = new GUIContent("+", "Load scene additively");
		private GUIContent m_ActiveSceneButtonTextCache = new GUIContent("*", "Active scene (cannot unload)");
		private GUIContent m_RemoveSceneButtonTextCache = new GUIContent("-", "Unload scene");

		public static bool AssetsChanged = false;


		private bool m_Initialized = false;

		// In big projects with 1k number of scenes, don't show everything.
		[NonSerialized]
		private bool m_ShowFullBigList = false;

		private Vector2 m_ScrollPos;
		private Vector2 m_ScrollPosPinned;
		private string m_Filter = string.Empty;
		private bool m_FocusFilterField = false; // HACK!

		private PersonalPreferences m_PersonalPrefs = new PersonalPreferences();

		[SerializeField]
		private List<string> m_ProjectExcludes = new List<string>();	// Exclude paths OR filenames (per project preference)
		private List<SceneEntry> m_Scenes = new List<SceneEntry>();
		private List<SceneEntry> m_Pinned = new List<SceneEntry>();     // NOTE: m_Scenes & m_Pinned are not duplicated
		private int m_PinnedGroupsCount = 0;

		private bool m_ShowPreferences = false;
		private const string PERSONAL_PREFERENCES_KEY = "ScenesInProject";
		private const string PROJECT_EXCLUDES_PATH = "ProjectSettings/ScenesInProject.Exclude.txt";

		private const string SettingsPathScenes = "Library/ScenesInProject.Scenes.txt";
		private const string SettingsPathPinnedScenes = "Library/ScenesInProject.PinnedScenes.txt";

		private void StorePinned()
		{
			File.WriteAllLines(SettingsPathPinnedScenes, m_Pinned.Select(e => e.Path));
		}

		private void StoreScenes()
		{
			File.WriteAllLines(SettingsPathScenes, m_Scenes.Select(e => e.Path));
		}

		private static bool RemoveRedundant(List<SceneEntry> list, List<string> scenesInDB)
		{
			bool removeSuccessful = false;

			for (int i = list.Count - 1; i >= 0; i--) {
				int sceneIndex = scenesInDB.IndexOf(list[i].Path);
				if (sceneIndex == -1) {
					list.RemoveAt(i);
					removeSuccessful = true;
				}
			}

			return removeSuccessful;
		}

		private void SortScenes(List<SceneEntry> list)
		{
			switch(m_PersonalPrefs.SortType) {
				case SortType.MostRecent:
					break;

				case SortType.ByFileName:
					list.Sort((a, b) => Path.GetFileNameWithoutExtension(a.Path).CompareTo(Path.GetFileNameWithoutExtension(b.Path)));
					break;

				case SortType.ByPath:
					list.Sort((a, b) => a.Path.CompareTo(b.Path));
					break;

				default: throw new NotImplementedException();
			}
		}

		private int RegroupScenes(List<SceneEntry> list)
		{
			int groupsCount = 0;

			// Grouping scenes with little entries looks silly. Don't do grouping.
			if (list.Count < 6) {
				foreach(var sceneEntry in list) {
					sceneEntry.FirstInGroup = false;
				}

				return groupsCount;
			}

			// Consider the following example of grouping.
			// foo1    1
			// foo2    2
			// foo3    3
			//
			// bar1    1
			// bar2    2
			// bar3    3
			//
			// pepo    1
			// gogo    1
			// lili    1
			//
			// zzz1    1
			// zzz2    2
			// zzz3    3
			// zzz4    4
			//
			// Roro8   1


			list.First().FirstInGroup = false;
			int entriesInGroup = 1;
			string prevFolder, currFolder, nextFolder;


			for (int i = 1; i < list.Count - 1; ++i) {
				prevFolder = list[i - 1].Folder;
				currFolder = list[i].Folder;
				nextFolder = list[i + 1].Folder;

				list[i].FirstInGroup = false;

				if (prevFolder == currFolder) {
					entriesInGroup++;
					continue;
				}

				if (currFolder == nextFolder) {
					list[i].FirstInGroup = true;
					groupsCount++;
					entriesInGroup = 1;
					continue;
				}

				if (entriesInGroup > 1) {
					list[i].FirstInGroup = true;
					groupsCount++;
					entriesInGroup = 1;
					continue;
				}
			}

			// Do last element
			prevFolder = list[list.Count - 2].Folder;
			currFolder = list[list.Count - 1].Folder;

			list.Last().FirstInGroup = entriesInGroup > 1 && prevFolder != currFolder;

			return groupsCount;
		}

		private void OnDisable()
		{
			if (m_Initialized) {
				StorePinned();
				StoreScenes();
			}
		}

		//
		// Load save settings
		//
		private void LoadData()
		{
			var personalPrefsData = EditorPrefs.GetString(PERSONAL_PREFERENCES_KEY, string.Empty);
			if (!string.IsNullOrEmpty(personalPrefsData)) {
				m_PersonalPrefs = JsonUtility.FromJson<PersonalPreferences>(personalPrefsData);
			} else {
				m_PersonalPrefs = new PersonalPreferences();
			}

			if (File.Exists(PROJECT_EXCLUDES_PATH)) {
				m_ProjectExcludes = new List<string>(File.ReadAllLines(PROJECT_EXCLUDES_PATH));
			} else {
				m_ProjectExcludes = new List<string>();
			}

			m_Pinned = new List<SceneEntry>(File.ReadAllLines(SettingsPathPinnedScenes).Select(line => new SceneEntry(line)));
			m_Scenes = new List<SceneEntry>(File.ReadAllLines(SettingsPathScenes).Select(line => new SceneEntry(line)));
		}

		private void InitializeData()
		{
			if (!m_Initialized) {
				LoadData();
			}

			//
			// Cache available scenes
			//
			string[] sceneGuids = AssetDatabase.FindAssets("t:Scene");
			var scenesInDB = new List<string>(sceneGuids.Length);
			foreach (string guid in sceneGuids) {
				string scenePath = AssetDatabase.GUIDToAssetPath(guid);

				if (ShouldExclude(m_ProjectExcludes, scenePath))
					continue;

				scenesInDB.Add(scenePath);
			}

			bool hasChanges = RemoveRedundant(m_Scenes, scenesInDB);
			hasChanges = RemoveRedundant(m_Pinned, scenesInDB) || hasChanges;

			foreach (string s in scenesInDB) {

				if (m_Scenes.Concat(m_Pinned).All(e => e.Path != s)) {
					m_Scenes.Add(new SceneEntry(s));

					hasChanges = true;
				}
			}


			if (hasChanges) {
				SortScenes(m_Scenes);

				StorePinned();
				StoreScenes();
			}

			RegroupScenes(m_Scenes);
			m_PinnedGroupsCount = RegroupScenes(m_Pinned);
		}

		private void InitializeStyles()
		{
			LEFT_ALIGNED_BUTTON = new GUIStyle(GUI.skin.button);
			LEFT_ALIGNED_BUTTON.alignment = TextAnchor.MiddleLeft;
			LEFT_ALIGNED_BUTTON.padding.left = 10;
		}



		private void OnGUI()
		{
			// Initialize on demand (not on OnEnable), to make sure everything is up and running.
			if (!m_Initialized || AssetsChanged) {
				InitializeData();
				InitializeStyles();
				m_Initialized = true;
				AssetsChanged = false;
			}

			if (m_ShowPreferences) {
				DrawPreferences();
				return;
			}

			EditorGUILayout.BeginHorizontal();

			bool openFirstResult;
			string[] filterWords;
			DrawControls(out openFirstResult, out filterWords);

			DrawSceneLists(openFirstResult, filterWords);

			EditorGUILayout.EndVertical();
		}

		private void DrawControls(out bool openFirstResult, out string[] filterWords)
		{
			//
			// Draw Filter
			//
			GUILayout.Label("Search:", EditorStyles.boldLabel, GUILayout.ExpandWidth(false));


			GUI.SetNextControlName("FilterControl");
			m_Filter = EditorGUILayout.TextField(m_Filter, GUILayout.Height(20));

			// HACK: skip a frame to focus control, to avoid visual bugs. Bad bad Unity!
			if (m_FocusFilterField) {
				GUI.FocusControl("FilterControl");
				m_FocusFilterField = false;
			}

			// Clear on ESC
			if (Event.current.isKey && Event.current.keyCode == KeyCode.Escape && GUI.GetNameOfFocusedControl() == "FilterControl") {
				m_Filter = "";
				GUI.FocusControl("");
				Event.current.Use();
			}

			// Clear on button
			if (GUILayout.Button("X", GUILayout.Width(20.0f))) {
				m_Filter = "";
				GUI.FocusControl("");
				m_FocusFilterField = true;
				Repaint();
			}

			if (GUILayout.Button(m_PreferencesButtonTextCache, GUILayout.Width(20.0f))) {
				m_ShowPreferences = true;
				GUIUtility.ExitGUI();
			}

			// Unfocus on enter. Open first scene from results.
			openFirstResult = false;
			if (Event.current.isKey && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) && GUI.GetNameOfFocusedControl() == "FilterControl") {
				GUI.FocusControl("");
				Repaint();
				Event.current.Use();

				if (!string.IsNullOrEmpty(m_Filter)) {
					openFirstResult = true;
				}

			}

			EditorGUILayout.EndHorizontal();

			filterWords = string.IsNullOrEmpty(m_Filter) ? null : m_Filter.Split(FILTER_WORD_SEPARATORS, StringSplitOptions.RemoveEmptyEntries);
			if (openFirstResult) {
				m_Filter = "";
			}
		}

		private void DrawSceneLists(bool openFirstResult, string[] filterWords)
		{
			//
			// Show pinned scenes
			//
			if (m_Pinned.Count > 0) {
				GUILayout.Label("Pinned:", EditorStyles.boldLabel);

				float scrollViewHeight;
				var shouldScroll = ShouldScrollPinned(filterWords, out scrollViewHeight);

				if (shouldScroll) {
					EditorGUILayout.BeginVertical();
					m_ScrollPosPinned = EditorGUILayout.BeginScrollView(m_ScrollPosPinned, false, false, GUILayout.Height(scrollViewHeight));
				}

				for (int i = 0; i < m_Pinned.Count; ++i) {
					var sceneEntry = m_Pinned[i];
					var sceneName = Path.GetFileNameWithoutExtension(sceneEntry.Path);
					if (!IsFilteredOut(sceneName, filterWords)) {

						if (sceneEntry.FirstInGroup && filterWords == null) {
							if (m_PersonalPrefs.SpaceBetweenGroupsPinned > 0) {
								GUILayout.Space(m_PersonalPrefs.SpaceBetweenGroupsPinned);
							}
						}

						DrawSceneButtons(sceneEntry, true, false);
					}
				}

				if (shouldScroll) {
					EditorGUILayout.EndScrollView();
					EditorGUILayout.EndVertical();
				}
			}


			//
			// Show all scenes
			//
			GUILayout.Label("Scenes:", EditorStyles.boldLabel);

			EditorGUILayout.BeginVertical();
			m_ScrollPos = EditorGUILayout.BeginScrollView(m_ScrollPos, false, false);

			var filteredCount = 0;
			for (var i = 0; i < m_Scenes.Count; i++) {
				var sceneEntry = m_Scenes[i];
				var sceneName = Path.GetFileNameWithoutExtension(sceneEntry.Path);

				// Do filtering
				if (IsFilteredOut(sceneName, filterWords))
					continue;

				filteredCount++;


				if (sceneEntry.FirstInGroup && filterWords == null) {
					if (m_PersonalPrefs.SpaceBetweenGroups > 0) {
						GUILayout.Space(m_PersonalPrefs.SpaceBetweenGroups);
					}
				}

				DrawSceneButtons(sceneEntry, false, openFirstResult);
				openFirstResult = false;

				if (!m_ShowFullBigList && filteredCount >= m_PersonalPrefs.ListCountLimit)
					break;
			}


			// Big lists support
			if (!m_ShowFullBigList && filteredCount >= m_PersonalPrefs.ListCountLimit && GUILayout.Button("... Show All ...", GUILayout.ExpandWidth(true))) {
				m_ShowFullBigList = true;
				GUIUtility.ExitGUI();
			}

			if (m_ShowFullBigList && filteredCount >= m_PersonalPrefs.ListCountLimit && GUILayout.Button("... Hide Some ...", GUILayout.ExpandWidth(true))) {
				m_ShowFullBigList = false;
				GUIUtility.ExitGUI();
			}

			EditorGUILayout.EndScrollView();
		}

		private bool ShouldScrollPinned(string[] filterWords, out float scrollViewHeight)
		{
			// Calculate pinned scroll view layout.
			const float LINE_PADDING = 6;
			float LINE_HEIGHT = EditorGUIUtility.singleLineHeight + LINE_PADDING;

			var scenesCount = (filterWords == null)
				? m_Pinned.Count
				: m_Pinned.Count(se => !IsFilteredOut(se.Name, filterWords));

			var pinnedTop = LINE_HEIGHT * 2 + 4; // Stuff before the pinned list (roughly).
			var pinnedGroupsSpace = filterWords == null ? m_PinnedGroupsCount * m_PersonalPrefs.SpaceBetweenGroupsPinned : 0;
			var pinnedTotalHeight = LINE_HEIGHT * scenesCount + pinnedGroupsSpace;

			scrollViewHeight = Mathf.Max(position.height * 0.6f - pinnedTop, LINE_HEIGHT * 3);
			return pinnedTotalHeight >= scrollViewHeight + LINE_PADDING;
		}

		private bool IsFilteredOut(string sceneName, string[] filterWords)
		{

			if (filterWords == null)
				return false;

			foreach (var filterWord in filterWords) {
				if (sceneName.IndexOf(filterWord, StringComparison.OrdinalIgnoreCase) == -1) {
					return true;
				}
			}

			return false;
		}

		private void MoveSceneAtTopOfList(SceneEntry sceneEntry)
		{
			int idx = m_Scenes.IndexOf(sceneEntry);
			if (idx >= 0) {
				m_Scenes.RemoveAt(idx);
				m_Scenes.Insert(0, sceneEntry);
			}

			RegroupScenes(m_Scenes);
		}

		private void DrawSceneButtons(SceneEntry sceneEntry, bool isPinned, bool forceOpen)
		{
			EditorGUILayout.BeginHorizontal();

			m_SceneButtonTextCache.text = m_PersonalPrefs.SceneDisplay == SceneDisplay.SceneNames ? sceneEntry.Name : sceneEntry.Path;
			m_SceneButtonTextCache.tooltip = sceneEntry.Path;

			var scene = SceneManager.GetSceneByPath(sceneEntry.Path);
			bool isSceneLoaded = scene.IsValid();
			bool isActiveScene = isSceneLoaded && scene == SceneManager.GetActiveScene();
			var loadedButton = isSceneLoaded ? (isActiveScene ? m_ActiveSceneButtonTextCache : m_RemoveSceneButtonTextCache) : m_AddSceneButtonTextCache;

			bool optionsPressed = GUILayout.Button(isPinned ? "O" : "@", GUILayout.Width(22));
			bool scenePressed = GUILayout.Button(m_SceneButtonTextCache, LEFT_ALIGNED_BUTTON) || forceOpen;
			bool loadPressed = GUILayout.Button(loadedButton, GUILayout.Width(20));


			if (scenePressed || optionsPressed || loadPressed) {
				// If scene was removed outside of Unity, the AssetModificationProcessor would not get notified.
				if (!File.Exists(sceneEntry.Path)) {
					AssetsChanged = true;
					return;
				}
			}

			if (scenePressed) {


				if (Event.current.shift) {
					EditorUtility.RevealInFinder(sceneEntry.Path);

				} else if (Application.isPlaying || EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) {

					if (Application.isPlaying) {
						// Note: to do this, the scene must me added to the build settings list.
						// Note2: Sometimes there are side effects with the lighting.
						SceneManager.LoadSceneAsync(sceneEntry.Path);
					} else {
						EditorSceneManager.OpenScene(sceneEntry.Path);
					}

					if (m_PersonalPrefs.SortType == SortType.MostRecent) {
						MoveSceneAtTopOfList(sceneEntry);
					}
					//m_Filter = "";	// It's a feature. Sometimes you need to press on multiple scenes in a row.
					GUI.FocusControl("");
				}
			}


			if (optionsPressed) {
				if (isPinned) {
					ShowPinnedOptions(sceneEntry);
				} else {
					PinScene(sceneEntry);
				}
			}

			if (loadPressed) {
				if (Application.isPlaying) {
					if (!isSceneLoaded) {
						// Note: to do this, the scene must me added to the build settings list.
						SceneManager.LoadScene(sceneEntry.Path, LoadSceneMode.Additive);
					} else if (!isActiveScene) {
						SceneManager.UnloadSceneAsync(scene);
					}
				} else {
					if (!isSceneLoaded) {
						EditorSceneManager.OpenScene(sceneEntry.Path, OpenSceneMode.Additive);

						//if (m_PersonalPrefs.SortType == SortType.MostRecent) {
						//	MoveSceneAtTopOfList(sceneEntry);
						//}
					} else if (!isActiveScene) {
						EditorSceneManager.CloseScene(scene, true);
					}
				}
			}

			EditorGUILayout.EndHorizontal();
		}


		private void PinScene(SceneEntry sceneEntry)
		{
			m_Scenes.Remove(sceneEntry);

			int pinIndex = m_Pinned.FindLastIndex(s => s.Folder == sceneEntry.Folder);
			if (pinIndex == -1) {
				m_Pinned.Add(sceneEntry);
			} else {
				m_Pinned.Insert(pinIndex + 1, sceneEntry);
			}

			// Don't sort m_Scenes or m_Pinned.
			RegroupScenes(m_Scenes);
			m_PinnedGroupsCount = RegroupScenes(m_Pinned);

			StorePinned();
			StoreScenes();
		}

		// Show context menu with options.
		private void ShowPinnedOptions(SceneEntry sceneEntry)
		{
			var menu = new GenericMenu();
			int index = m_Pinned.IndexOf(sceneEntry);

			foreach (PinnedOptions value in Enum.GetValues(typeof(PinnedOptions))) {
				menu.AddItem(new GUIContent(ObjectNames.NicifyVariableName(value.ToString())), false, OnSelectPinnedOption, new KeyValuePair<PinnedOptions, int>(value, index));
			}

			menu.ShowAsContext();
		}

		private void OnSelectPinnedOption(object data)
		{
			var pair = (KeyValuePair<PinnedOptions, int>)data;
			int index = pair.Value;
			SceneEntry temp;

			switch (pair.Key) {

				case PinnedOptions.Unpin:
					m_Scenes.Insert(0, m_Pinned[index]);
					m_Pinned.RemoveAt(index);
					break;

				case PinnedOptions.MoveUp:
					if (index == 0)
						return;

					temp = m_Pinned[index];
					m_Pinned[index] = m_Pinned[index - 1];
					m_Pinned[index - 1] = temp;
					break;

				case PinnedOptions.MoveDown:
					if (index == m_Pinned.Count - 1)
						return;

					temp = m_Pinned[index];
					m_Pinned[index] = m_Pinned[index + 1];
					m_Pinned[index + 1] = temp;
					break;

				case PinnedOptions.MoveFirst:
					m_Pinned.Insert(0, m_Pinned[index]);
					m_Pinned.RemoveAt(index + 1);
					break;

				case PinnedOptions.MoveLast:
					m_Pinned.Add(m_Pinned[index]);
					m_Pinned.RemoveAt(index);
					break;

				case PinnedOptions.ShowInExplorer:
					EditorUtility.RevealInFinder(m_Pinned[index].Path);
					break;

				case PinnedOptions.ShowInProject:
					EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(m_Pinned[index].Path));
					break;
			}

			SortScenes(m_Scenes);

			RegroupScenes(m_Scenes);
			m_PinnedGroupsCount = RegroupScenes(m_Pinned);

			StorePinned();
			StoreScenes();

			Repaint();
		}


		private Vector2 m_PreferencesScroll;
		private void DrawPreferences()
		{
			EditorGUILayout.Space();

			//
			// Save / Close Buttons
			//
			EditorGUILayout.BeginHorizontal();
			{
				EditorGUILayout.LabelField("Save changes:", EditorStyles.boldLabel);

				GUILayout.FlexibleSpace();

				var prevColor = GUI.backgroundColor;
				GUI.backgroundColor = Color.green / 1.2f;
				if (GUILayout.Button("Save And Close", GUILayout.MaxWidth(150f))) {
					SaveAndClosePreferences();

					GUI.FocusControl("");
					EditorGUIUtility.ExitGUI();
				}
				GUI.backgroundColor = prevColor;
			}
			EditorGUILayout.EndHorizontal();

			EditorGUILayout.Space();

			//
			// Personal Preferences
			//
			EditorGUILayout.LabelField("Personal Preferences:", EditorStyles.boldLabel);
			{
				EditorGUILayout.HelpBox("Hint: check the the tooltips.", MessageType.Info);

				m_PersonalPrefs.SortType = (SortType) EditorGUILayout.EnumPopup(new GUIContent("Sort By", "How to sort the list of scenes (not the pinned ones).\nNOTE: Changing this might override the \"Most Recent\" sort done by now."), m_PersonalPrefs.SortType);
				m_PersonalPrefs.SceneDisplay = (SceneDisplay) EditorGUILayout.EnumPopup(new GUIContent("Display", "How scenes should be displayed."), m_PersonalPrefs.SceneDisplay);

				m_PersonalPrefs.ListCountLimit = EditorGUILayout.IntField(new GUIContent("Shown scenes limit", "If the scenes in the list are more than this value, they will be truncated (button \"Show All\" is shown).\nTruncated scenes still participate in the search.\n\nThis is very useful in a project with lots of scenes, where drawing large scrollable lists is expensive."), m_PersonalPrefs.ListCountLimit);
				m_PersonalPrefs.ListCountLimit = Mathf.Clamp(m_PersonalPrefs.ListCountLimit, 0, 1024); // More round.

				const string spaceBetweenGroupsHint = "Space in pixels added before every group of scenes.\nScenes in the same folder are considered as a group.";
				m_PersonalPrefs.SpaceBetweenGroups = EditorGUILayout.IntField(new GUIContent("Padding for groups", spaceBetweenGroupsHint), m_PersonalPrefs.SpaceBetweenGroups);
				m_PersonalPrefs.SpaceBetweenGroups = Mathf.Clamp(m_PersonalPrefs.SpaceBetweenGroups, 0, (int)EditorGUIUtility.singleLineHeight);

				m_PersonalPrefs.SpaceBetweenGroupsPinned = EditorGUILayout.IntField(new GUIContent("Padding for pinned groups", spaceBetweenGroupsHint), m_PersonalPrefs.SpaceBetweenGroupsPinned);
				m_PersonalPrefs.SpaceBetweenGroupsPinned = Mathf.Clamp(m_PersonalPrefs.SpaceBetweenGroupsPinned, 0, 16); // More round.
			}

			//
			// Project Preferences
			//
			EditorGUILayout.LabelField("Project Preferences:", EditorStyles.boldLabel);
			{
				EditorGUILayout.HelpBox("These settings will be saved in the ProjectSettings folder.\nFeel free to add them to your version control system.\nCoordinate any changes here with your team.", MessageType.Warning);

				m_PreferencesScroll = EditorGUILayout.BeginScrollView(m_PreferencesScroll);

				var so = new SerializedObject(this);
				var sp = so.FindProperty("m_ProjectExcludes");

				EditorGUILayout.PropertyField(sp, new GUIContent("Exclude paths", "Asset paths that will be ignored."), true);

				so.ApplyModifiedProperties();

				EditorGUILayout.EndScrollView();
			}
		}

		private void SaveAndClosePreferences()
		{
			m_ProjectExcludes.RemoveAll(string.IsNullOrWhiteSpace);

			// Sort explicitly, so assets will change on reload.
			SortScenes(m_Scenes);

			EditorPrefs.SetString(PERSONAL_PREFERENCES_KEY, JsonUtility.ToJson(m_PersonalPrefs));

			File.WriteAllLines(PROJECT_EXCLUDES_PATH, m_ProjectExcludes);
			m_ShowPreferences = false;
			AssetsChanged = true;
		}

		// NOTE: Copy pasted from SearchAssetsFilter.
		private static bool ShouldExclude(IEnumerable<string> excludes, string path)
		{
			foreach(var exclude in excludes) {

				bool isExcludePath = exclude.Contains('/');    // Check if this is a path or just a filename

				if (isExcludePath) {
					if (path.StartsWith(exclude, StringComparison.OrdinalIgnoreCase))
						return true;

				} else {

					var filename = Path.GetFileName(path);
					if (filename.IndexOf(exclude, StringComparison.OrdinalIgnoreCase) != -1)
						return true;
				}
			}

			return false;
		}
	}

	//
	// Monitors scene assets for any modifications and signals to the ScenesViewWindow.
	//
	internal class ScenesInProjectChangeProcessor : UnityEditor.AssetModificationProcessor
	{

		// NOTE: This won't be called when duplicating a scene. Unity says so!
		// More info: https://issuetracker.unity3d.com/issues/assetmodificationprocessor-is-not-notified-when-an-asset-is-duplicated
		// The only way to get notified is via AssetPostprocessor, but that gets called for everything (saving scenes including).
		// Check the implementation below.
		public static void OnWillCreateAsset(string path)
		{
			if (path.EndsWith(".unity.meta") || path.EndsWith(".unity"))
				ScenesInProject.AssetsChanged = true;
		}

		public static AssetDeleteResult OnWillDeleteAsset(string path, RemoveAssetOptions option)
		{
			if (path.EndsWith(".unity.meta") || path.EndsWith(".unity"))
				ScenesInProject.AssetsChanged = true;

			return AssetDeleteResult.DidNotDelete;
		}

		public static AssetMoveResult OnWillMoveAsset(string oldPath, string newPath)
		{
			if (oldPath.EndsWith(".unity.meta") || oldPath.EndsWith(".unity"))
				ScenesInProject.AssetsChanged = true;

			return AssetMoveResult.DidNotMove;
		}
	}

	// NOTE: This gets called for every asset change including saving scenes.
	// This might have small performance hit for big projects so don't use it.
	//internal class ScenesInProjectAssetPostprocessor : AssetPostprocessor
	//{
	//	private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
	//	{
	//		if (importedAssets.Any(path => path.EndsWith(".unity"))) {
	//			ScenesInProject.AssetsChanged = true;
	//		}
	//	}
	//}

}

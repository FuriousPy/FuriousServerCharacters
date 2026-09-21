using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;
using UnityEngine;

namespace ServerCharacters;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInIncompatibility("org.bepinex.plugins.valheim_plus")]
public class ServerCharacters : BaseUnityPlugin
{
	private const string ModName = "Server Characters";
	private const string ModVersion = "1.4.46";
	private const string ModGUID = "org.bepinex.plugins.servercharacters";

	public static ServerCharacters selfReference = null!;
	public static ManualLogSource logger => selfReference.Logger;
	private static readonly Harmony harmony = new(ModGUID);

	private float fixedUpdateCount = 0;
	private int localSnapshotSeconds = 0;
	private FileSystemWatcher? characterTemplateWatcher;
	public static int monotonicCounter = 0;

	public const int CharacterNameDisconnectMagic = 498209834;
	public const int SingleCharacterModeDisconnectMagic = 845979243;

	public static readonly ConfigSync configSync = new(ModGUID) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

	private static ConfigEntry<Toggle> serverConfigLocked = null!;
	public static ConfigEntry<Toggle> singleCharacterMode = null!;
	public static ConfigEntry<Toggle> backupOnlyMode = null!;
	public static ConfigEntry<Toggle> hardcoreMode = null!;
	public static ConfigEntry<int> autoSaveInterval = null!;
	public static ConfigEntry<int> disconnectProtectionInterval = null!;
	public static ConfigEntry<int> afkKickTimer = null!;
	public static ConfigEntry<string> loginMessage = null!;
	public static ConfigEntry<string> firstLoginMessage = null!;
	public static ConfigEntry<string> serverKey = null!;
	public static ConfigEntry<Intro> newCharacterIntro = null!;
	public static ConfigEntry<Toggle> storePoison = null!;

	public static readonly CustomSyncedValue<string> playerTemplate = new(configSync, "PlayerTemplate", readCharacterTemplate());

	private static string pluginDir => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

	private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
	{
		ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

		SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
		syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

		return configEntry;
	}

	private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);

	private static void ApplyAutoSaveInterval()
	{
		try
		{
			Game.m_saveInterval = autoSaveInterval.Value * 60f;
		}
		catch (Exception e)
		{
			logger.LogError($"Could not apply the configured character and world autosave interval; keeping Valheim's current interval: {e}");
		}
	}

	private class ConfigurationManagerAttributes
	{
		[UsedImplicitly]
		public bool? Browsable = false;
	}

	public void Awake()
	{
		selfReference = this;
		serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
		configSync.AddLockingConfigEntry(serverConfigLocked);
		afkKickTimer = config("1 - General", "AFK Kick Timer", 0, new ConfigDescription("Automatically kicks players, if they haven't moved at all in the configured time. In minutes. 0 is disabled.", new AcceptableValueRange<int>(0, 30)));
		loginMessage = config("1 - General", "Login Message", "I have arrived!", new ConfigDescription("Message to shout on login. Leave empty to not shout anything."));

		hardcoreMode = config("2 - Save Files", "Hardcore mode", Toggle.Off, "If set to on, players will be kicked from the server and their save file on the server will be deleted, if they die.");
		singleCharacterMode = config("2 - Save Files", "Single Character Mode", Toggle.Off, "If set to on, each SteamID / Xbox ID can create one character only on this server. Has no effect for admins.");
		backupOnlyMode = config("2 - Save Files", "Backup only mode", Toggle.Off, "Enabling this will not enforce the server profile anymore. DO NOT ENABLE THIS IF YOU DON'T KNOW EXACTLY WHAT YOU ARE DOING!");
		autoSaveInterval = config("2 - Save Files", "Auto save interval", 30, new ConfigDescription("Minutes between auto saves of characters and the world.", new AcceptableValueRange<int>(1, 120)));
		autoSaveInterval.SettingChanged += (_, _) => ApplyAutoSaveInterval();
		ApplyAutoSaveInterval();
		disconnectProtectionInterval = config("2 - Save Files", "Unexpected disconnect protection interval", 60, new ConfigDescription("Seconds between full character snapshots cached in server memory. A cached snapshot is written to disk only after an unexpected disconnect.", new AcceptableValueRange<int>(15, 300)));
		storePoison = config("2 - Save Files", "Store poison debuff", Toggle.On, new ConfigDescription("If on, poison debuffs are stored in the save file on logout and applied on login, to prevent users from logging out if they are poisoned, to clear the debuff."));

		firstLoginMessage = config("3 - First Login", "First Login Message", "A new player logged in for the first time: {name}", new ConfigDescription("Message to display if a player logs in for the very first time. Leave empty to not display anything."));
		newCharacterIntro = config("3 - First Login", "Intro", Intro.ValkyrieAndIntro, new ConfigDescription("Sets the kind of intro new characters will get."));

		serverKey = config("4 - Other", "Server key", "", new ConfigDescription("DO NOT TOUCH THIS! DO NOT SHARE THIS! Encryption key used for emergency profile backups. DO NOT SHARE THIS! DO NOT TOUCH THIS!", null, new ConfigurationManagerAttributes()), false);

		Assembly assembly = Assembly.GetExecutingAssembly();
		harmony.PatchAll(assembly);
		Application.wantsToQuit += ServerSide.OnApplicationWantsToQuit;

		try
		{
			characterTemplateWatcher = new FileSystemWatcher(pluginDir, "CharacterTemplate.yml");
			characterTemplateWatcher.Created += templateFileEvent;
			characterTemplateWatcher.Changed += templateFileEvent;
			characterTemplateWatcher.Renamed += templateFileEvent;
			characterTemplateWatcher.Deleted += templateFileEvent;
			characterTemplateWatcher.IncludeSubdirectories = true;
			characterTemplateWatcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
			characterTemplateWatcher.EnableRaisingEvents = true;
		}
		catch (Exception e)
		{
			Logger.LogError($"Could not monitor CharacterTemplate.yml for changes; continuing with the startup value: {e}");
			characterTemplateWatcher?.Dispose();
			characterTemplateWatcher = null;
		}

		ServerSide.generateServerKey();

		harmony.Patch(AccessTools.DeclaredMethod(typeof(FejdStartup), nameof(Awake)), postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ServerCharacters), nameof(Initialize))));
	}

	private void OnDestroy()
	{
		Application.wantsToQuit -= ServerSide.OnApplicationWantsToQuit;
		try
		{
			characterTemplateWatcher?.Dispose();
		}
		catch (Exception e)
		{
			Logger.LogError($"Could not dispose the character-template watcher cleanly: {e}");
		}
		finally
		{
			characterTemplateWatcher = null;
		}
	}

	public static void Initialize()
	{
		harmony.Unpatch(AccessTools.DeclaredMethod(typeof(FejdStartup), nameof(Awake)), HarmonyPatchType.Postfix, harmony.Id);
		try
		{
			InitializeProfiles();
		}
		catch (Exception e)
		{
			logger.LogError($"Could not initialize the server character directory or profile cache; the server will continue running: {e}");
		}
	}

	private static void InitializeProfiles()
	{
		Directory.CreateDirectory(Utils.CharacterSavePath);

		string legacyPath = SaveSystem.GetCharacterFolderPath(FileHelpers.FileSource.Legacy);
		if (Directory.Exists(legacyPath))
		{
			foreach (string s in Directory.GetFiles(legacyPath))
			{
				FileInfo file = new(s);
				if (Utils.IsServerCharactersFilePattern(file.Name) || file.Name == "backups")
				{
					Directory.Move(file.FullName, Utils.CharacterSavePath + Path.DirectorySeparatorChar + file.Name);
				}
			}
		}

		foreach (string s in Directory.GetFiles(Utils.CharacterSavePath))
		{
			FileInfo file = new(s);
			if (file.Name.EndsWith(".fch", StringComparison.Ordinal) && Regex.IsMatch(file.Name.Split('_')[0], @"^\d+$"))
			{
				string newPath = file.DirectoryName + Path.DirectorySeparatorChar + "Steam_" + file.Name;
				file.MoveTo(newPath);
			}
		}

		foreach (string s in Directory.GetFiles(Utils.CharacterSavePath))
		{
			FileInfo file = new(s);
			if (Utils.IsServerCharactersFilePattern(file.Name))
			{
				Utils.ProfileName profileName = new();

				string[] parts = file.Name.Split('_');
				profileName.id = $"{parts[0]}_{parts[1]}";
				profileName.name = parts[2].Split('.')[0];
				PlayerProfile profile = new(file.Name.Replace(".fch", ""), FileHelpers.FileSource.Local);
				profile.Load();
				Utils.Cache.profiles[profileName] = profile;
			}
		}
	}

	private static void templateFileEvent(object s, EventArgs e)
	{
		try
		{
			playerTemplate.AssignLocalValue(readCharacterTemplate());
		}
		catch (Exception exception)
		{
			logger.LogError($"Could not reload CharacterTemplate.yml; keeping the previous template: {exception}");
		}
	}

	private static string readCharacterTemplate()
	{
		try
		{
			string templatePath = pluginDir + Path.DirectorySeparatorChar + "CharacterTemplate.yml";
			return File.Exists(templatePath) ? File.ReadAllText(templatePath) : "";
		}
		catch (Exception e)
		{
			selfReference?.Logger.LogError($"Could not read CharacterTemplate.yml; using an empty template: {e}");
			return "";
		}
	}

	private void FixedUpdate()
	{
		const float timerInterval = 1f;
		fixedUpdateCount += Time.fixedDeltaTime;
		if ((double)fixedUpdateCount < timerInterval)
		{
			return;
		}

		if (ZNet.instance?.IsServer() != true && ClientSide.serverCharacter)
		{
			if (++localSnapshotSeconds >= 10)
			{
				localSnapshotSeconds = 0;
				ClientSide.snapShotProfile();
			}
			ClientSide.SendDisconnectProtectionSnapshot();
		}
		else
		{
			localSnapshotSeconds = 0;
		}

		fixedUpdateCount -= timerInterval;
		++monotonicCounter;
		ServerSide.RetryDisconnectedSaves();
	}
}

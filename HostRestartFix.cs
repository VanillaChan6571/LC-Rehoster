using System;
using System.Collections;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;
using System.Linq;

namespace HostRestartFix
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        // Logger needs to be accessible for static methods
        internal static ManualLogSource logger;
        private static bool firstHostDetected = false;
        private static bool hasBeenForceQuit = false;
        private static GameObject messageObject;
        private static TMPro.TextMeshProUGUI messageText;
        internal static Plugin Instance;  // Changed to internal for debugging

        // Configuration for manual restart key
        internal static ConfigEntry<Key> RestartKey = null!;
        private static bool keyPressedNow = false;

        private void Awake()
        {
            // Plugin startup logic
            logger = Logger;
            logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
            
            // Store instance reference for coroutine access
            Instance = this;
            logger.LogInfo("Plugin instance set");
            
            // Set up configuration
            RestartKey = Config.Bind<Key>("General", "RestartKey", Key.R, 
                "This key, when pressed in combination with Ctrl+Shift+Alt, will force return to main menu (useful if mods break)");

            try
            {
                // Apply Harmony patch manually to avoid ambiguity with overloaded methods
                Harmony harmony = new Harmony(PluginInfo.PLUGIN_GUID);
                
                // Get the specific Debug.Log method that takes a single object parameter
                var originalMethod = AccessTools.Method(typeof(UnityEngine.Debug), "Log", new Type[] { typeof(object) });
                if (originalMethod == null)
                {
                    logger.LogError("Failed to find Debug.Log method to patch!");
                    return;
                }
                
                // Get our prefix method
                var prefixMethod = AccessTools.Method(typeof(DebugLogPatch), "Prefix");
                if (prefixMethod == null)
                {
                    logger.LogError("Failed to find prefix method for patching!");
                    return;
                }
                
                // Apply the patch
                harmony.Patch(originalMethod, new HarmonyMethod(prefixMethod));
                logger.LogInfo("Debug.Log patch applied successfully");
                
                // Apply additional patches for keybind functionality
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                logger.LogInfo("All patches applied successfully");
            }
            catch (Exception ex)
            {
                logger.LogError($"Failed to apply Harmony patch: {ex.Message}");
                logger.LogError(ex.StackTrace);
                return; // Early return if patching fails
            }

            try {
                // Create a persistent GameObject for messages
                GameObject messageCanvas = new GameObject("RestartFixMessageCanvas");
                DontDestroyOnLoad(messageCanvas);
                Canvas canvas = messageCanvas.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 10000; // Ensure it's on top

                // Set up the text object
                messageObject = new GameObject("RestartFixMessage");
                messageObject.transform.SetParent(canvas.transform, false);

                RectTransform rect = messageObject.AddComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(800, 100);

                messageText = messageObject.AddComponent<TMPro.TextMeshProUGUI>();
                messageText.fontSize = 24;
                messageText.alignment = TMPro.TextAlignmentOptions.Center;
                messageText.color = Color.red;
                messageText.text = "";
                
                // Hide message initially
                messageObject.SetActive(false);
                logger.LogInfo("UI components created successfully");
            } catch (Exception ex) {
                logger.LogError($"Error creating UI: {ex.Message}");
            }
            
            try {
                // Create keybind handler component
                GameObject keyHandlerObj = new GameObject("ReHosterKeyHandler");
                keyHandlerObj.AddComponent<KeybindHandlerBehaviour>();
                DontDestroyOnLoad(keyHandlerObj);
                logger.LogInfo("Keybind handler created successfully");
            } catch (Exception ex) {
                logger.LogError($"Error creating keybind handler: {ex.Message}");
            }
            
            // Reset flags on load
            hasBeenForceQuit = false;
            firstHostDetected = false;
            logger.LogInfo("Flags reset: hasBeenForceQuit=" + hasBeenForceQuit);
        }
        
        private void Update()
        {
            // Backup method to check keybinds if the dedicated component hasn't been created
            if (Keyboard.current != null)
            {
                CheckForceRestartKeybind();
            }
        }
        
        // Method to check if the force restart keybind is pressed
        public static void CheckForceRestartKeybind()
        {
            if (Keyboard.current == null) return;
            
            if (Keyboard.current.ctrlKey.isPressed && 
                Keyboard.current.altKey.isPressed && 
                Keyboard.current.shiftKey.isPressed)
            {
                KeyControl restartKeyControl = Keyboard.current.allKeys.FirstOrDefault(key => 
                    key.keyCode == RestartKey.Value);
                    
                if (restartKeyControl != null && restartKeyControl.isPressed)
                {
                    if (!keyPressedNow)
                    {
                        logger.LogInfo("Force restart keybind pressed!");
                        ForceReturnToMainMenu();
                    }
                    keyPressedNow = true;
                }
                else
                {
                    keyPressedNow = false;
                }
            }
            else
            {
                keyPressedNow = false;
            }
        }
        
        // Method to force return to main menu (can be called by keybind or automatically)
        public static void ForceReturnToMainMenu()
        {
            logger.LogInfo("ForceReturnToMainMenu called");
            
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
            {
                try 
                { 
                    // First try the clean disconnect through GameNetworkManager
                    GameNetworkManager.Instance?.Disconnect();
                    logger.LogInfo("Attempted clean disconnect through GameNetworkManager");
                }
                catch (Exception ex) 
                {
                    logger.LogError($"Error during disconnect: {ex.Message}");
                }
                
                try 
                { 
                    // Force shutdown NetworkManager as a backup
                    NetworkManager.Singleton.Shutdown();
                    logger.LogInfo("Force shutdown NetworkManager");
                }
                catch (Exception ex) 
                {
                    logger.LogError($"Error during NetworkManager shutdown: {ex.Message}");
                }
            }
            else 
            {
                logger.LogInfo("Not connected to network or NetworkManager.Singleton is null");
            }
            
            // Reset game values to default using reflection (like QuickQuitToMenu does)
            if (GameNetworkManager.Instance != null)
            {
                try
                {
                    var resetMethod = typeof(GameNetworkManager).GetMethod("ResetGameValuesToDefault", 
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    
                    if (resetMethod != null)
                    {
                        resetMethod.Invoke(GameNetworkManager.Instance, new object[] { });
                        logger.LogInfo("Reset game values to default");
                    }
                    else
                    {
                        logger.LogError("ResetGameValuesToDefault method not found");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"Error resetting game values: {ex.Message}");
                }
            }
            else
            {
                logger.LogInfo("GameNetworkManager.Instance is null");
            }
            
            // Load main menu scene
            try
            {
                logger.LogInfo("Attempting to load main menu scene by index");
                SceneManager.LoadScene(0); // Main menu scene index
                logger.LogInfo("Loaded main menu scene");
            }
            catch (Exception ex)
            {
                logger.LogError($"Error loading main menu scene: {ex.Message}");
                try
                {
                    logger.LogInfo("Attempting to load main menu scene by name");
                    SceneManager.LoadScene("MainMenu"); // Try by name as fallback
                    logger.LogInfo("Loaded main menu scene by name");
                }
                catch (Exception ex2)
                {
                    logger.LogError($"Error loading main menu scene by name: {ex2.Message}");
                }
            }
        }

        // Patch class for Debug.Log
        private class DebugLogPatch
        {
            static void Prefix(object message)
            {
                if (message == null) return;
        
                string msgStr = message.ToString();
        
                // Log hosting-related messages for debugging
                if (msgStr.Contains("host") && !msgStr.Contains("ghost"))
                {
                    logger.LogInfo($"Host-related message detected: '{msgStr}'");
                }
        
                // Check for host start message
                if (msgStr.Contains("[Info   : Unity Log] started host!"))
                {
                    logger.LogInfo($"Host start message detected: '{msgStr}'");
            
                    if (!hasBeenForceQuit && !firstHostDetected)
                    {
                        logger.LogInfo("First host detected! Waiting for client to finish loading...");
                        firstHostDetected = true;
                        // We don't start the timer yet, just mark that hosting has started
                    }
                }
        
                // Check for loading completion message
                if (msgStr.Contains("[Info   : Unity Log] Has beta save data:") && firstHostDetected && !hasBeenForceQuit)
                {
                    logger.LogInfo("Client has finished loading! Starting countdown timer now.");
            
                    if (Instance == null)
                    {
                        logger.LogError("Plugin Instance is null! Cannot start coroutine!");
                        return;
                    }
            
                    try
                    {
                        logger.LogInfo("Starting ForceQuitToMenu coroutine");
                        Instance.StartCoroutine(ForceQuitToMenu());
                        logger.LogInfo("Coroutine started successfully");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($"Failed to start coroutine: {ex.Message}");
                        logger.LogError(ex.StackTrace);
                
                        // Try immediate force return if coroutine fails
                        hasBeenForceQuit = true;
                        ForceReturnToMainMenu();
                    }
                }
            }
        }

        // Coroutine with proper error handling that doesn't use try/catch around yields
        private static IEnumerator ForceQuitToMenu()
        {
            logger.LogInfo("ForceQuitToMenu coroutine started");
            
            // Show message - with error checking before each critical operation
            if (messageObject != null)
            {
                messageObject.SetActive(true);
                if (messageText != null)
                {
                    messageText.text = "The Mods have Pre-loaded. Returning to menu in 10 seconds.\nPlease re-host the game for proper weather loading.";
                }
                logger.LogInfo("Message displayed");
            }
            else
            {
                logger.LogError("messageObject is null!");
            }

            // Wait 10 seconds
            float timer = 10f;
            while (timer > 0)
            {
                if (messageText != null)
                {
                    messageText.text = $"The Mods have Pre-loaded. Returning to menu in {timer:0} seconds.\nPlease re-host the game for proper weather loading.";
                }
                logger.LogInfo($"Timer: {timer}");
                yield return new WaitForSeconds(1f);
                timer--;
            }

            // Force quit to menu
            logger.LogInfo("Timer completed, setting hasBeenForceQuit=true");
            hasBeenForceQuit = true;
            
            if (messageObject != null)
            {
                messageObject.SetActive(false);
            }
            
            // Use the clean force quit method
            logger.LogInfo("Calling ForceReturnToMainMenu");
            ForceReturnToMainMenu();
            
            yield return new WaitForSeconds(2f);
            
            // Show completion message briefly
            logger.LogInfo("Showing completion message");
            if (messageObject != null && messageText != null)
            {
                messageObject.SetActive(true);
                messageText.text = "Please start hosting again for proper weather loading.";
                
                yield return new WaitForSeconds(5f);
                messageObject.SetActive(false);
            }
            logger.LogInfo("ForceQuitToMenu coroutine completed successfully");
        }
    }
    
    // Component to handle keybinds
    public class KeybindHandlerBehaviour : MonoBehaviour
    {
        void Update()
        {
            Plugin.CheckForceRestartKeybind();
        }
    }

    // Harmony patches to keep behavior active during game scenes
    [HarmonyPatch]
    internal class ReHosterPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(StartOfRound), "Start")]
        public static void InGameSetup()
        {
            // Ensure our behavior is active in-game
            GameObject keyHandlerObj = GameObject.Find("ReHosterKeyHandler");
            if (keyHandlerObj == null)
            {
                keyHandlerObj = new GameObject("ReHosterKeyHandler");
                keyHandlerObj.AddComponent<KeybindHandlerBehaviour>();
                UnityEngine.Object.DontDestroyOnLoad(keyHandlerObj);
                Plugin.logger.LogInfo("Created KeybindHandlerBehaviour in game scene");
            }
        }
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID = "net.vanilla.hostrestartfix";
        public const string PLUGIN_NAME = "HostRestartFix";
        public const string PLUGIN_VERSION = "1.0.0";
    }
}
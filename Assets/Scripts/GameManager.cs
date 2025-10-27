using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine.UI;

namespace KTEspana
{
    public class GameManager : MonoBehaviour
    {
        private static readonly Regex KillRegex = new Regex(
            @"^<(?<time>[^>]+)>\s+\[Notice\]\s+<Actor Death>\s+CActor::Kill:\s+'(?<victim>[^']+)'\s+\[(?<victimId>\d+)\]\s+in zone\s+'(?<zone>[^']+)'\s+killed by\s+'(?<killer>[^']+)'\s+\[(?<killerId>\d+)\]\s+using\s+'(?<weapon>[^']+)'\s+\[Class\s+(?<weaponClass>[^\]]+)\]\s+with damage type\s+'(?<damage>[^']+)'\s+from direction x:\s*(?<dx>[-\d\.]+),\s*y:\s*(?<dy>[-\d\.]+),\s*z:\s*(?<dz>[-\d\.]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex LoginRegex = new Regex(
            @"<(?<timestamp>[^>]+)> \[Notice\] <AccountLoginCharacterStatus_Character> Character: createdAt (?<createdAt>\d+) - updatedAt (?<updatedAt>\d+) - geid (?<geid>\d+) - accountId (?<accountId>\d+) - name (?<name>\w+) - state (?<state>\w+)",
            RegexOptions.Compiled
        );
        
        private const string ShipJsonUrl = "https://github.com/Romezzn/basurilla/releases/download/json/ships.json";

        private static readonly string[] CommonPaths =
        {
            @"C:\Program Files\Roberts Space Industries\StarCitizen\LIVE\Game.log",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"rsilauncher\logs\log.log"),
        };

        private static readonly HttpClient client = new HttpClient();

        private bool recording;
        public GameObject SendingKills;
        public TMP_InputField discordId;
        public TMP_InputField gamelogPath;
        public TMP_Dropdown Dropdown;
        public TextMeshProUGUI errorMessage;
        private long lastPos;
        private bool loginDone;
        private bool scanningForLogin = true; // nuevo flag
        private string gameUserId;
        private CancellationTokenSource cts;

        private void Start()
        {
            GetShips().Forget();
            gamelogPath.text = FindLogFile();
        }

        public void StartRecording() => StartRecordingAsync().Forget();

        public void StopRecording()
        {
            recording = false;
            cts?.Cancel();
            SendingKills.SetActive(false);
        }

        public void OpenGetDiscordId()
        {
            Application.OpenURL("https://support.discord.com/hc/es/articles/206346498--D%C3%B3nde-puedo-encontrar-mi-ID-de-usuario-servidor-mensaje");
        }

        private async UniTaskVoid StartRecordingAsync()
        {
            recording = true;

            if (string.IsNullOrEmpty(discordId.text) || string.IsNullOrEmpty(gamelogPath.text))
            {
                Debug.LogError("❌ No se encontró ningún archivo de log o no se introdujo el Discord ID.");
                errorMessage.gameObject.SetActive(true);
                return;
            }

            cts?.Dispose();
            cts = new CancellationTokenSource();

            Debug.Log($"📄 Observando log en: {gamelogPath.text}");
            SendingKills.SetActive(true);

            // Empezamos desde 0 para buscar el login
            lastPos = 0;
            scanningForLogin = true;
            loginDone = false;

            await MonitorLogFile(gamelogPath.text);
        }
        
        private async UniTask GetShips()
        {
            string url = "https://github.com/Romezzn/basurilla/releases/download/json/ships.json";
            string json = await client.GetStringAsync(url);

            var shipsList = JsonConvert.DeserializeObject<ShipList>(json);
            Dropdown.AddOptions(shipsList.ships);
        }

        private string FindLogFile()
        {
            foreach (var p in CommonPaths)
            {
                if (File.Exists(p))
                {
                    Debug.Log("Encontrado log en: " + p);
                    return p;
                }
            }
            return null;
        }

        private async UniTask MonitorLogFile(string path)
        {
            while (recording)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs, Encoding.UTF8, true);

                    if (!scanningForLogin && fs.Length < lastPos)
                    {
                        Debug.LogWarning("⚠️ Log truncado o rotado, reiniciando al final.");
                        lastPos = fs.Length;
                    }

                    fs.Seek(lastPos, SeekOrigin.Begin);

                    string line;
                    while (!cts.Token.IsCancellationRequested && (line = await sr.ReadLineAsync()) != null)
                    {
                        lastPos = fs.Position;
                        if (!recording) break;

                        if (!loginDone)
                        {
                            var loginMatch = LoginRegex.Match(line);
                            if (loginMatch.Success)
                            {
                                loginDone = true;
                                scanningForLogin = false;
                                gameUserId = loginMatch.Groups["name"].Value;
                                Debug.Log($"✅ Login detectado: {gameUserId}");

                                // Saltar al final del archivo una vez encontrado el login
                                lastPos = fs.Length;
                                Debug.Log("➡️ Saltando al final del log. Monitorizando kills en tiempo real.");
                                break;
                            }
                            continue;
                        }

                        // Procesar kills solo después del login
                        var killMatch = KillRegex.Match(line);
                        if (!killMatch.Success) continue;

                        var fecha = DateTimeOffset.Parse(killMatch.Groups["time"].Value);
                        var evt = new KillTrackerData
                        {
                            timestamp = fecha.ToUnixTimeSeconds(),
                            victim = killMatch.Groups["victim"].Value,
                            killer = killMatch.Groups["killer"].Value,
                            zone = killMatch.Groups["zone"].Value,
                            weapon = killMatch.Groups["weapon"].Value,
                            damageType = killMatch.Groups["damage"].Value
                        };

                        Debug.Log($"💀 Kill detectada: {evt.killer} → {evt.victim}");

                        await UniTask.Delay(1000, cancellationToken: cts.Token);
                        await SendToDiscordBotEspana(evt);
                    }
                }
                catch (OperationCanceledException)
                {
                    Debug.Log("⏹ Lectura cancelada.");
                    return;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ERR] {ex.Message}");
                }

                await UniTask.Delay(300, cancellationToken: cts.Token);
            }
        }

        private async UniTask SendToDiscordBotEspana(KillTrackerData evtObj)
        {
            var payload = new
            {
                discordUserId = discordId.text,
                handleName = gameUserId,
                evtObj.timestamp,
                evtObj.victim,
                evtObj.killer,
                evtObj.zone,
                evtObj.weapon,
                evtObj.damageType,
                Dropdown.itemText.text,
                evtObj.eventoId
            };

            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Authorization", "Bearer ...");

            var response = await client.PostAsync("http://sg.dimzo.es:10000/api/kills", content);
            var result = await response.Content.ReadAsStringAsync();

            Debug.Log($"📤 Status: {response.StatusCode}");
            Debug.Log($"Response: {result}");
        }
    }
}

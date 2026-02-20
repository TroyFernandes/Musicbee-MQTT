using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MQTTnet;
using MQTTnet.Client;
using System.Threading;
using System.Threading.Tasks;
using Nito.AsyncEx.Synchronous;
using Nito.AsyncEx;
using System.Xml;
using Newtonsoft.Json.Linq;
using System.Text;
using Newtonsoft.Json;
using MQTTnet.Protocol;

namespace MusicBeePlugin
{
    public partial class Plugin
    {

        private MusicBeeApiInterface mbApiInterface;
        private PluginInfo about = new PluginInfo();

        IMqttClient mqttClient;
        MqttClientOptions mqttOptions;
        private volatile bool _closing;
        private System.Threading.Timer _progressTimer;
        private CancellationTokenSource _shutdownCts = new CancellationTokenSource();
        
        // Event handler delegates to enable unsubscription
        private Func<MqttClientDisconnectedEventArgs, Task> _disconnectedHandler;
        private Func<MqttClientConnectedEventArgs, Task> _connectedHandler;
        private Func<MqttApplicationMessageReceivedEventArgs, Task> _messageReceivedHandler;

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            mbApiInterface = new MusicBeeApiInterface();
            mbApiInterface.Initialise(apiInterfacePtr);
            about.PluginInfoVersion = PluginInfoVersion;
            about.Name = "MQTT";
            about.Description = "Send current playing song to a MQTT Server";
            about.Author = "TF";
            about.TargetApplication = "";   //  the name of a Plugin Storage device or panel header for a dockable panel
            about.Type = PluginType.General;
            about.VersionMajor = 2;  // your plugin version
            about.VersionMinor = 0;
            about.Revision = 3;
            about.MinInterfaceVersion = MinInterfaceVersion;
            about.MinApiRevision = MinApiRevision;
            about.ReceiveNotifications = (ReceiveNotificationFlags.PlayerEvents | ReceiveNotificationFlags.TagEvents);
            about.ConfigurationPanelHeight = 0;   // height in pixels that musicbee should reserve in a panel for config settings. When set, a handle to an empty panel will be passed to the Configure function

            _ = ConnectMQTTAsync();

            return about;
        }

        public bool Configure(IntPtr panelHandle)
        {
            // save any persistent settings in a sub-folder of this path
            string dataPath = mbApiInterface.Setting_GetPersistentStoragePath();
            // panelHandle will only be set if you set about.ConfigurationPanelHeight to a non-zero value
            // keep in mind the panel width is scaled according to the font the user has selected
            // if about.ConfigurationPanelHeight is set to 0, you can display your own popup window
            if (panelHandle != IntPtr.Zero)
            {
                Panel configPanel = (Panel)Panel.FromHandle(panelHandle);
                Label prompt = new Label();
                prompt.AutoSize = true;
                prompt.Location = new Point(0, 0);
                prompt.Text = "prompt:";
                TextBox textBox = new TextBox();
                textBox.Bounds = new Rectangle(60, 0, 100, textBox.Height);
                configPanel.Controls.AddRange(new Control[] { prompt, textBox });
            }



            return false;
        }

        public void SaveSettings()
        {
            // save any persistent settings in a sub-folder of this path
            string dataPath = mbApiInterface.Setting_GetPersistentStoragePath();
        }

        public void Close(PluginCloseReason reason)
        {
			_closing = true;

			//publish offline state to MQTT before shutdown, do not wait
			if (mqttClient != null && mqttClient.IsConnected)
			{
				PublishAsync(mqttClient, "musicbee/player/state", "idle");
				PublishAsync(mqttClient, "musicbee/player/status", "offline");
			}

			// Cancel any pending reconnect delay immediately
			try { _shutdownCts?.Cancel(); } catch { }

			// Stop the progress timer first
			_progressTimer?.Dispose();
			_progressTimer = null;

            if (mqttClient != null)
            {
                try
                {
                    // CRITICAL: Unsubscribe event handlers FIRST to prevent them from firing during shutdown
                    if (_disconnectedHandler != null)
                        mqttClient.DisconnectedAsync -= _disconnectedHandler;
                    if (_connectedHandler != null)
                        mqttClient.ConnectedAsync -= _connectedHandler;
                    if (_messageReceivedHandler != null)
                        mqttClient.ApplicationMessageReceivedAsync -= _messageReceivedHandler;

                    // For clean shutdown, skip graceful disconnect - just dispose immediately
                    // This prevents any async handlers from blocking the shutdown
                }
                catch { }
                finally
                {
                    // Force dispose regardless of state
                    try { mqttClient?.Dispose(); } catch { }
                    mqttClient = null;
                }
            }

            try { _shutdownCts?.Dispose(); } catch { }

            // Force garbage collection to clean up any remaining async continuations
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        public void Uninstall()
        {
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            // Don't process any notifications during shutdown
            if (_closing) return;
            
            // perform some action depending on the notification type
            switch (type)
            {
                case NotificationType.PlayingTracksChanged:
                    PublishAsync(mqttClient, "musicbee/player/qsize", GetQueueSize(false)).WaitWithoutException();
                    break;
                case NotificationType.PluginStartup:
                    // perform startup initialisation
                    PublishAsync(mqttClient, "musicbee/player/qsize", GetQueueSize(false)).WaitWithoutException();
                    PublishRetainedAsync(mqttClient, "musicbee/player/state", GetPlayerStateString()).WaitWithoutException();
                    PublishOutputDevices();

                    // Start periodic progress timer (every 1 second)
                    _progressTimer = new System.Threading.Timer(ProgressTimerCallback, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                    break;
                case NotificationType.TrackChanged:

                    PublishAsync(mqttClient, "musicbee/song/album", mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Album)).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/song/albumart", mbApiInterface.NowPlaying_GetArtwork()).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/song/title", mbApiInterface.NowPlaying_GetFileTag(MetaDataType.TrackTitle)).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/song/artist", mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Artist)).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/song/info", GetTrackInfo()).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/player/volume", GetVolumeString()).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/player/file", mbApiInterface.NowPlaying_GetFileUrl()).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/player/position", (mbApiInterface.NowPlayingList_GetCurrentIndex() + 1).ToString()).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/player/shuffle", GetShuffleString()).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/player/repeat", GetRepeatModeString()).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/song/duration", (mbApiInterface.NowPlaying_GetDuration() / 1000).ToString()).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/player/progress", (mbApiInterface.Player_GetPosition() / 1000).ToString()).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/player/muted", mbApiInterface.Player_GetMute().ToString().ToLower()).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/song/track", mbApiInterface.NowPlaying_GetFileTag(MetaDataType.TrackNo)).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/song/genre", mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Genre)).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/song/albumartist", mbApiInterface.NowPlaying_GetFileTag(MetaDataType.AlbumArtist)).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/song/year", mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Year)).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/song/content_type", "music").WaitWithoutException();

                    break;
                case NotificationType.PlayStateChanged:
                    PublishRetainedAsync(mqttClient, "musicbee/player/state", GetPlayerStateString()).WaitWithoutException();
                    break;
                case NotificationType.VolumeLevelChanged:
                    PublishAsync(mqttClient, "musicbee/player/volume", GetVolumeString()).WaitWithoutException();
                    break;
                case NotificationType.PlayerShuffleChanged:
                    PublishAsync(mqttClient, "musicbee/player/shuffle", GetShuffleString()).WaitWithoutException();
                    break;
                case NotificationType.PlayerRepeatChanged:
                    PublishAsync(mqttClient, "musicbee/player/repeat", GetRepeatModeString()).WaitWithoutException();
                    break;
                case NotificationType.VolumeMuteChanged:
                    PublishAsync(mqttClient, "musicbee/player/muted", mbApiInterface.Player_GetMute().ToString().ToLower()).WaitWithoutException();
                    break;
            }
        }

        private void ProgressTimerCallback(object state)
        {
            if (_closing) return;
            try
            {
                var playState = mbApiInterface.Player_GetPlayState();
                if (playState == PlayState.Playing)
                {
                    var positionSeconds = (mbApiInterface.Player_GetPosition() / 1000).ToString();
                    PublishAsync(mqttClient, "musicbee/player/progress", positionSeconds).WaitWithoutException();
                }
            }
            catch
            {
                // Ignore — MusicBee may be shutting down
            }
        }

        private string GetShuffleString()
        {
            // HA only understands true/false for shuffle.
            // MusicBee has off/shuffle/auto-dj. Treat autodj as shuffle=true.
            if (mbApiInterface.Player_GetAutoDjEnabled())
                return "true";
            return mbApiInterface.Player_GetShuffle().ToString().ToLower();
        }

        private void PublishOutputDevices()
        {
            try
            {
                if (mbApiInterface.Player_GetOutputDevices(out string[] deviceNames, out string activeDevice))
                {
                    var json = JsonConvert.SerializeObject(deviceNames);
                    PublishRetainedAsync(mqttClient, "musicbee/player/output_devices", json).WaitWithoutException();
                    PublishRetainedAsync(mqttClient, "musicbee/player/output_device", activeDevice ?? "").WaitWithoutException();
                }
            }
            catch { }
        }

        private string GetVolumeString()
        {
            return ((int)(mbApiInterface.Player_GetVolume() * 100)).ToString();
        }

        private string GetRepeatModeString()
        {
            switch (mbApiInterface.Player_GetRepeat())
            {
                case RepeatMode.All: return "all";
                case RepeatMode.One: return "one";
                default: return "off";
            }
        }

        private string GetPlayerStateString()
        {
            switch (mbApiInterface.Player_GetPlayState())
            {
                case PlayState.Playing: return "playing";
                case PlayState.Paused: return "paused";
                case PlayState.Loading: return "buffering";
                case PlayState.Stopped: return "idle";
                default: return "idle";
            }
        }

        static async Task PublishAsync(IMqttClient client, string topic, string payload)
        {
            if (client?.IsConnected != true)
                return;

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce)
                .Build();

            await client.PublishAsync(message, CancellationToken.None);
        }

        static async Task PublishRetainedAsync(IMqttClient client, string topic, string payload)
        {
            if (client?.IsConnected != true)
                return;

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce)
                .WithRetainFlag(true)
                .Build();

            await client.PublishAsync(message, CancellationToken.None);
        }

        private void handleCommand(MqttApplicationMessageReceivedEventArgs x)
        {
            try
            {
                JObject jObject = JObject.Parse(x.ApplicationMessage.ConvertPayloadToString());
                string command = jObject["command"].Value<string>();

                switch (command)
                {

                    case "pause":
                        if (mbApiInterface.Player_GetPlayState() == PlayState.Playing)
                        {
                            mbApiInterface.Player_PlayPause();
                        }
                        break;
                    case "play":
                        if (mbApiInterface.Player_GetPlayState() == PlayState.Paused || mbApiInterface.Player_GetPlayState() == PlayState.Stopped)
                        {
                            mbApiInterface.Player_PlayPause();
                        }
                        break;
                    case "stop":
                        mbApiInterface.Player_Stop();
                        break;
                    case "next":
                        mbApiInterface.Player_PlayNextTrack();
                        break;
                    case "previous":
                        mbApiInterface.Player_PlayPreviousTrack();
                        break;
                    case "volume_set":
                        {
                            var volume = jObject["args"]["volume"].Value<float>();
                            if (volume > 1.0f)
                            {
                                volume = volume / 100f;
                            }
                            mbApiInterface.Player_SetVolume(volume);
                        }
                        break;
                    case "shuffle_set":
                        {
                            var shuffle = jObject["args"]["shuffle"].Value<bool>();
                            if (shuffle)
                            {
                                // If AutoDJ is active, end it and set normal shuffle
                                if (mbApiInterface.Player_GetAutoDjEnabled())
                                    mbApiInterface.Player_EndAutoDj();
                                mbApiInterface.Player_SetShuffle(true);
                            }
                            else
                            {
                                // Turn off shuffle and AutoDJ
                                if (mbApiInterface.Player_GetAutoDjEnabled())
                                    mbApiInterface.Player_EndAutoDj();
                                mbApiInterface.Player_SetShuffle(false);
                            }
                        }
                        break;
                    case "repeat_set":
                        {
                            var repeat = jObject["args"]["repeat"].Value<string>();
                            switch (repeat)
                            {
                                case "all":
                                    mbApiInterface.Player_SetRepeat(RepeatMode.All);
                                    break;
                                case "one":
                                    mbApiInterface.Player_SetRepeat(RepeatMode.One);
                                    break;
                                default:
                                    mbApiInterface.Player_SetRepeat(RepeatMode.None);
                                    break;
                            }
                        }
                        break;
                    case "seek":
                        {
                            var position = jObject["args"]["position"].Value<int>();
                            mbApiInterface.Player_SetPosition(position * 1000);
                            // Publish the new position back to MQTT
                            PublishAsync(mqttClient, "musicbee/player/progress", position.ToString()).WaitWithoutException();
                        }
                        break;
                    case "mute_toggle":
                        {
                            mbApiInterface.Player_SetMute(!mbApiInterface.Player_GetMute());
                        }
                        break;
                    case "mute_set":
                        {
                            var mute = jObject["args"]["mute"].Value<bool>();
                            mbApiInterface.Player_SetMute(mute);
                        }
                        break;
                    case "select_source":
                        {
                            var source = jObject["args"]["source"].Value<string>();
                            mbApiInterface.Player_SetOutputDevice(source);
                            PublishAsync(mqttClient, "musicbee/player/output_device", source).WaitWithoutException();
                        }
                        break;
                }
            }
            catch (JsonReaderException)
            {
                // Ignore malformed JSON commands
            }


        }

        private (string, int, string, string) ReadSettings()
        {
            var path = mbApiInterface.Setting_GetPersistentStoragePath();
            if (System.IO.File.Exists(path + @"\MB_MQTT_Settings.xml"))
            {
                XmlDocument doc = new XmlDocument();
                doc.Load(path + @"\MB_MQTT_Settings.xml");
                var addr = doc.GetElementsByTagName("server_address")[0].InnerText;
                var port = doc.GetElementsByTagName("server_port")[0].InnerText;
                var user = doc.GetElementsByTagName("server_username")[0].InnerText;
                var pass = doc.GetElementsByTagName("server_password")[0].InnerText;

                return (addr, int.Parse(port), user, pass);
            }

            return (null, -1, null, null);
        }

        private string GetQueueSize(bool next)
        {
            mbApiInterface.NowPlayingList_QueryFilesEx("", out string[] temp);
            if (next)
            {
                return temp[mbApiInterface.NowPlayingList_GetCurrentIndex() + 1];
            }
            return temp.Length.ToString();
        }

        private string GetTrackInfo()
        {
            StringBuilder s = new StringBuilder();

            s.Append(mbApiInterface.NowPlaying_GetFileProperty(FilePropertyType.Kind).Replace(" audio file", ""));
            s.Append(" ");
            s.Append(mbApiInterface.NowPlaying_GetFileProperty(FilePropertyType.SampleRate));
            s.Append(", ");
            s.Append(mbApiInterface.NowPlaying_GetFileProperty(FilePropertyType.Bitrate));

            return s.ToString();
        }

        public async Task ConnectMQTTAsync()
        {
            var (addr, port, user, pass) = ReadSettings();
            if (addr == null) return;

            var factory = new MqttFactory();
            mqttClient = factory.CreateMqttClient();

            mqttOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(addr, port)
                .WithCredentials(user, pass)
                .WithCleanSession()
                .Build();

            // Auto-reconnect on disconnect (only if not closing)
            _disconnectedHandler = async e =>
            {
                if (_closing) return;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _shutdownCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return; // Shutdown requested during delay
                }
                if (_closing) return;
                try
                {
                    await mqttClient.ConnectAsync(mqttOptions, _shutdownCts.Token);
                }
                catch
                {
                    // Connection failed — will retry on next disconnect event
                }
            };

            _connectedHandler = async e =>
            {
                if (_closing) return;
                // (Re)subscribe and publish status on every successful connection
                await mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic("musicbee/command").Build());

                await PublishRetainedAsync(mqttClient, "musicbee/player/status", "online");
                await PublishRetainedAsync(mqttClient, "musicbee/player/state", "idle");
            };

            _messageReceivedHandler = e =>
            {
                if (_closing) return Task.CompletedTask;
                handleCommand(e);
                return Task.CompletedTask;
            };

            mqttClient.DisconnectedAsync += _disconnectedHandler;
            mqttClient.ConnectedAsync += _connectedHandler;
            mqttClient.ApplicationMessageReceivedAsync += _messageReceivedHandler;

            try
            {
                await mqttClient.ConnectAsync(mqttOptions);
            }
            catch
            {
                // MQTT server unreachable at startup — reconnect loop will handle it
            }
        }

    }

}
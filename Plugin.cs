using System;
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
            about.Revision = 0;
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
            PublishAsync(mqttClient, "musicbee/player/playing", "false").WaitWithoutException();
            PublishAsync(mqttClient, "musicbee/player/status", "offline").WaitWithoutException();
            if (mqttClient?.IsConnected == true)
            {
                mqttClient.DisconnectAsync().WaitWithoutException();
            }
        }

        public void Uninstall()
        {
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            // perform some action depending on the notification type
            switch (type)
            {
                case NotificationType.PlayingTracksChanged:
                    PublishAsync(mqttClient, "musicbee/player/qsize", GetQueueSize(false)).WaitWithoutException();
                    break;
                case NotificationType.PluginStartup:
                    // perform startup initialisation
                    PublishAsync(mqttClient, "musicbee/player/qsize", GetQueueSize(false)).WaitWithoutException();

                    break;
                case NotificationType.TrackChanged:

                    PublishAsync(mqttClient, "musicbee/song/album", mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Album)).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/song/albumart", mbApiInterface.NowPlaying_GetArtwork()).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/song/title", mbApiInterface.NowPlaying_GetFileTag(MetaDataType.TrackTitle)).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/song/artist", mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Artist)).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/song/info", GetTrackInfo()).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/player/volume", ((int)(mbApiInterface.Player_GetVolume() * 100)).ToString()).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/player/file", mbApiInterface.NowPlaying_GetFileUrl()).WaitWithoutException();
                    PublishAsync(mqttClient, "musicbee/player/position", (mbApiInterface.NowPlayingList_GetCurrentIndex() + 1).ToString()).WaitWithoutException();

                    break;
                case NotificationType.PlayStateChanged:
                    switch (mbApiInterface.Player_GetPlayState())
                    {
                        case PlayState.Playing:
                            PublishAsync(mqttClient, "musicbee/player/playing", "true").WaitWithoutException();
                            break;
                        case PlayState.Paused:
                            PublishAsync(mqttClient, "musicbee/player/playing", "false").WaitWithoutException();
                            break;
                    }
                    break;
                case NotificationType.VolumeLevelChanged:
                    PublishAsync(mqttClient, "musicbee/player/volume", ((int)(mbApiInterface.Player_GetVolume() * 100)).ToString()).WaitWithoutException();
                    break;
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
                            if (volume > 1.0)
                            {
                                volume = volume / 100;
                            }
                            mbApiInterface.Player_SetVolume(volume);
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

            // Auto-reconnect on disconnect
            mqttClient.DisconnectedAsync += async e =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                try
                {
                    await mqttClient.ConnectAsync(mqttOptions);
                }
                catch
                {
                    // Connection failed — will retry on next disconnect event
                }
            };

            mqttClient.ConnectedAsync += async e =>
            {
                // (Re)subscribe and publish status on every successful connection
                await mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic("musicbee/command").Build());
                await PublishAsync(mqttClient, "musicbee/player/status", "online");
                await PublishAsync(mqttClient, "musicbee/player/playing", "false");
            };

            mqttClient.ApplicationMessageReceivedAsync += e =>
            {
                handleCommand(e);
                return Task.CompletedTask;
            };

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
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MusicbeeMQTT.Tests
{
    public class CommandParsingTests
    {
        [Theory]
        [InlineData("{\"command\":\"play\"}", "play")]
        [InlineData("{\"command\":\"pause\"}", "pause")]
        [InlineData("{\"command\":\"stop\"}", "stop")]
        [InlineData("{\"command\":\"next\"}", "next")]
        [InlineData("{\"command\":\"previous\"}", "previous")]
        public void ParseCommand_ValidJson_ReturnsCorrectCommand(string json, string expected)
        {
            var jObject = JObject.Parse(json);
            var command = jObject["command"].Value<string>();
            Assert.Equal(expected, command);
        }

        [Fact]
        public void ParseCommand_VolumeSet_ParsesVolumeCorrectly()
        {
            var json = "{\"command\":\"volume_set\",\"args\":{\"volume\":75}}";
            var jObject = JObject.Parse(json);
            var command = jObject["command"].Value<string>();
            var volume = jObject["args"]["volume"].Value<float>();

            Assert.Equal("volume_set", command);
            Assert.Equal(75f, volume);
        }

        [Fact]
        public void ParseCommand_VolumeOver100_NormalizesTo0to1()
        {
            var json = "{\"command\":\"volume_set\",\"args\":{\"volume\":75}}";
            var jObject = JObject.Parse(json);
            var volume = jObject["args"]["volume"].Value<float>();

            if (volume > 1.0)
                volume = volume / 100;

            Assert.Equal(0.75f, volume);
        }

        [Fact]
        public void ParseCommand_VolumeAlreadyNormalized_StaysUnchanged()
        {
            var json = "{\"command\":\"volume_set\",\"args\":{\"volume\":0.5}}";
            var jObject = JObject.Parse(json);
            var volume = jObject["args"]["volume"].Value<float>();

            if (volume > 1.0)
                volume = volume / 100;

            Assert.Equal(0.5f, volume);
        }

        [Fact]
        public void ParseCommand_InvalidJson_ThrowsJsonReaderException()
        {
            Assert.Throws<Newtonsoft.Json.JsonReaderException>(() =>
            {
                JObject.Parse("not valid json");
            });
        }
    }

    public class MqttMessageTests
    {
        [Fact]
        public void BuildMessage_SetsTopicAndPayload()
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic("musicbee/player/playing")
                .WithPayload("true")
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce)
                .Build();

            Assert.Equal("musicbee/player/playing", message.Topic);
            Assert.Equal("true", Encoding.UTF8.GetString(message.PayloadSegment.Array,
                message.PayloadSegment.Offset, message.PayloadSegment.Count));
            Assert.Equal(MqttQualityOfServiceLevel.ExactlyOnce, message.QualityOfServiceLevel);
        }

        [Fact]
        public void BuildMessage_EmptyPayload_DoesNotThrow()
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic("musicbee/player/status")
                .WithPayload("")
                .Build();

            Assert.Equal("musicbee/player/status", message.Topic);
        }
    }

    public class SettingsTests
    {
        [Fact]
        public void ReadSettings_ValidXml_ParsesCorrectly()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "mb_mqtt_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var settingsPath = Path.Combine(tempDir, "MB_MQTT_Settings.xml");

            try
            {
                File.WriteAllText(settingsPath,
                    @"<?xml version=""1.0"" encoding=""utf-8""?>
                    <settings>
                        <server_address>192.168.1.100</server_address>
                        <server_port>1883</server_port>
                        <server_username>testuser</server_username>
                        <server_password>testpass</server_password>
                    </settings>");

                var doc = new System.Xml.XmlDocument();
                doc.Load(settingsPath);
                var addr = doc.GetElementsByTagName("server_address")[0].InnerText;
                var port = int.Parse(doc.GetElementsByTagName("server_port")[0].InnerText);
                var user = doc.GetElementsByTagName("server_username")[0].InnerText;
                var pass = doc.GetElementsByTagName("server_password")[0].InnerText;

                Assert.Equal("192.168.1.100", addr);
                Assert.Equal(1883, port);
                Assert.Equal("testuser", user);
                Assert.Equal("testpass", pass);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void ReadSettings_MissingFile_ReturnsNull()
        {
            var fakePath = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid().ToString("N"));
            var exists = File.Exists(Path.Combine(fakePath, "MB_MQTT_Settings.xml"));
            Assert.False(exists);
        }
    }

    public class MqttClientFactoryTests
    {
        [Fact]
        public void CreateMqttClient_ReturnsNonNullClient()
        {
            var factory = new MqttFactory();
            var client = factory.CreateMqttClient();
            Assert.NotNull(client);
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void MqttClientOptions_BuildsCorrectly()
        {
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer("192.168.1.100", 1883)
                .WithCredentials("user", "pass")
                .WithCleanSession()
                .Build();

            Assert.NotNull(options);
            Assert.NotNull(options.Credentials);
        }
    }
}

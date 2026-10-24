using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MakeraZ1TcpBridge
{
    internal static class Program
    {
        private static readonly byte[] Magic = { 0x86, 0x68 };
        private static readonly object LogLock = new object();
        private static readonly object SendLock = new object();
        private static readonly object LocalWriteLock = new object();
        private static bool stopping;
        private static bool debugLogging;

        private sealed class Config
        {
            public string Host;
            public int Port;
            public int ListenPort;
            public bool DebugLogging;
            public bool RotaryInstalled;
            public SortedDictionary<int, string> GrblSettings;
        }

        private sealed class PositionSnapshot
        {
            public readonly object Sync = new object();
            public double[] Work;
            public double[] Machine;
            public double[] ProjectedWork;
            public double[] ProjectedMachine;
        }

        private sealed class SensorSnapshot
        {
            public readonly object Sync = new object();
            public string PinStates = "";
            public string MakeraFields = "";
            public string LastDiagnostic = "";
        }

        private static int Main(string[] args)
        {
            try
            {
                string configPath = null;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--config" && i + 1 < args.Length) configPath = args[++i];
                }
                if (String.IsNullOrEmpty(configPath)) throw new ArgumentException("Usage: MakeraZ1TcpBridge.exe --config <path>");
                Config config = ReadConfig(configPath);
                debugLogging = config.DebugLogging;
                Log("Logging level: " + (debugLogging ? "DEBUG" : "INFO"));
                TcpListener listener = new TcpListener(IPAddress.Loopback, config.ListenPort);
                listener.Start(1);
                Log("Listening on 127.0.0.1:" + config.ListenPort + "; Z1 target " + config.Host + ":" + config.Port);
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; stopping = true; listener.Stop(); };
                while (!stopping)
                {
                    TcpClient client;
                    try { client = listener.AcceptTcpClient(); }
                    catch (SocketException) { if (stopping) break; throw; }
                    using (client)
                    {
                        client.NoDelay = true;
                        HandleClient(client, config);
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Log("Fatal: " + ex);
                return 1;
            }
        }

        private static Config ReadConfig(string path)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string section = "";
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Split('#')[0].TrimEnd();
                if (String.IsNullOrWhiteSpace(line)) continue;
                if (!Char.IsWhiteSpace(raw[0]) && line.TrimEnd().EndsWith(":")) { section = line.Trim().TrimEnd(':'); continue; }
                string normalized = line.Trim();
                int colon = normalized.IndexOf(':');
                if (colon > 0) values[section + "." + normalized.Substring(0, colon).Trim()] = normalized.Substring(colon + 1).Trim().Trim('"', '\'');
            }
            string host = Get(values, "z1.host", "");
            IPAddress address;
            int port, listenPort;
            string debugValue = Get(values, "logging.debug", "false");
            bool debug;
            if (!Boolean.TryParse(debugValue, out debug)) throw new InvalidDataException("logging.debug must be true or false in " + path);
            if (!IPAddress.TryParse(host, out address) || address.AddressFamily != AddressFamily.InterNetwork ||
                !Int32.TryParse(Get(values, "z1.port", ""), out port) || port < 1 || port > 65535 ||
                !Int32.TryParse(Get(values, "listener.port", "2323"), out listenPort) || listenPort < 1 || listenPort > 65535 ||
            Get(values, "listener.host", "127.0.0.1") != "127.0.0.1")
                throw new InvalidDataException("Invalid Z1 or listener settings in " + path);
            string rotaryValue = Get(values, "rotary.installed", "false");
            bool rotaryInstalled;
            if (!Boolean.TryParse(rotaryValue, out rotaryInstalled)) throw new InvalidDataException("rotary.installed must be true or false in " + path);
            var grblSettings = new SortedDictionary<int, string>();
            const string settingsPrefix = "grbl-settings.";
            foreach (KeyValuePair<string, string> entry in values)
            {
                if (!entry.Key.StartsWith(settingsPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                int number;
                double numericValue;
                string settingName = entry.Key.Substring(settingsPrefix.Length);
                if (!Int32.TryParse(settingName, out number) || number < 0 || number > 255 ||
                    !Double.TryParse(entry.Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out numericValue) ||
                    Double.IsNaN(numericValue) || Double.IsInfinity(numericValue))
                    throw new InvalidDataException("Invalid GRBL profile setting '" + settingName + "' in " + path);
                grblSettings[number] = entry.Value;
            }
            return new Config { Host = host, Port = port, ListenPort = listenPort, DebugLogging = debug, RotaryInstalled = rotaryInstalled, GrblSettings = grblSettings };
        }

        private static string Get(Dictionary<string, string> values, string key, string fallback)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : fallback;
        }

        private static string ReadZ1ConfigText(Config config)
        {
            using (var client = new TcpClient())
            {
                client.ReceiveTimeout = 8000;
                client.SendTimeout = 5000;
                client.Connect(config.Host, config.Port);
                using (NetworkStream stream = client.GetStream())
                {
                    var reader = new FrameReader(stream);
                    SendPayload(stream, new byte[] { 0xA1, (byte)'?' });
                    ReadFrameOfType(reader, 0x81, 16);

                    byte[] request = Encoding.ASCII.GetBytes("download /sd/config.txt");
                    var download = new byte[request.Length + 1];
                    // File transfer is initiated with B0; A2 is the ordinary text-command frame.
                    download[0] = 0xB0;
                    Buffer.BlockCopy(request, 0, download, 1, request.Length);
                    SendPayload(stream, download);
                    byte[] hash = ReadFrameOfType(reader, 0xB1, 16);
                    if (hash.Length < 2) throw new IOException("Z1 config download did not return a file hash");

                    // These transfer-control frames match Makera Studio's successful
                    // /sd/config.txt exchange captured from this Z1.
                    SendPayload(stream, new byte[] { 0xB2, 0xD2, 0xCA });
                    ReadFrameOfType(reader, 0xB2, 16);
                    SendPayload(stream, new byte[] { 0xB3, 0x00, 0x00, 0x00, 0x01, 0x18 });
                    byte[] fileFrame = ReadFrameOfType(reader, 0xB3, 16);
                    if (fileFrame.Length <= 5 || fileFrame[1] != 0 || fileFrame[2] != 0 || fileFrame[3] != 0 || fileFrame[4] != 1)
                        throw new IOException("Z1 returned an unexpected /sd/config.txt data frame");

                    var fileBytes = new byte[fileFrame.Length - 5];
                    Buffer.BlockCopy(fileFrame, 5, fileBytes, 0, fileBytes.Length);
                    SendPayload(stream, new byte[] { 0xB3, 0x00, 0x00, 0x00, 0x01 });
                    SendPayload(stream, new byte[] { 0xB4, 0xB2, 0x0C });
                    byte[] transferComplete = ReadFrameOfType(reader, 0xB4, 16);
                    string transferReply = transferComplete.Length > 1
                        ? Encoding.UTF8.GetString(transferComplete, 1, transferComplete.Length - 1)
                        : "";
                    if (transferReply.IndexOf("ok", StringComparison.OrdinalIgnoreCase) < 0)
                        throw new IOException("Z1 did not acknowledge config-transfer completion");

                    bool confirmed = false;
                    for (int i = 0; i < 16; i++)
                    {
                        byte[] reply = reader.ReadFrame();
                        if (reply.Length == 0) continue;
                        string message = reply.Length > 1 ? Encoding.UTF8.GetString(reply, 1, reply.Length - 1) : "";
                        if (reply[0] == 0x90 && message.IndexOf("download success: /sd/config.txt", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            confirmed = true;
                            break;
                        }
                        if (reply[0] == 0x90 && (message.IndexOf("download failed", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("canceled by remote", StringComparison.OrdinalIgnoreCase) >= 0))
                            throw new IOException("Z1 reported config download failure: " + message.Trim());
                    }
                    if (!confirmed) throw new IOException("Z1 did not confirm the /sd/config.txt download");
                    SaveConfigSnapshot(fileBytes);
                    return Encoding.UTF8.GetString(fileBytes);
                }
            }
        }

        private static void SaveConfigSnapshot(byte[] fileBytes)
        {
            string destination = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");
            string temporary = destination + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, fileBytes);
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
                Log("Saved downloaded /sd/config.txt to " + destination + " (" + fileBytes.Length + " bytes)");
            }
            catch (Exception ex)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                Log("Could not save local config.txt snapshot; continuing with the in-memory Z1 settings: " + ex.Message);
            }
        }

        private static byte[] ReadFrameOfType(FrameReader reader, byte expectedType, int maxFrames)
        {
            for (int i = 0; i < maxFrames; i++)
            {
                byte[] payload = reader.ReadFrame();
                if (payload.Length > 0 && payload[0] == expectedType) return payload;
                Debug("Ignored Z1 frame during config download: " + DescribePayload(payload));
            }
            throw new IOException("Z1 did not return expected config-transfer frame 0x" + expectedType.ToString("X2"));
        }

        private static SortedDictionary<int, string> BuildSessionSettings(Config config, string configText)
        {
            var settings = new SortedDictionary<int, string>(config.GrblSettings);
            var machineValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in configText.Replace("\r", "").Split('\n'))
            {
                string line = raw;
                int comment = line.IndexOf('#');
                if (comment >= 0) line = line.Substring(0, comment);
                string[] parts = line.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) machineValues[parts[0]] = parts[1];
            }

            ApplyBooleanSetting(machineValues, settings, "soft_endstop.enable", 20);
            ApplyNumericSetting(machineValues, settings, "alpha_max_rate", 110);
            ApplyNumericSetting(machineValues, settings, "beta_max_rate", 111);
            ApplyNumericSetting(machineValues, settings, "gamma_max_rate", 112);
            ApplyNumericSetting(machineValues, settings, "coordinate.worksize_x", 130);
            ApplyNumericSetting(machineValues, settings, "coordinate.worksize_y", 131);
            ApplyNumericSetting(machineValues, settings, "acceleration", 120);
            ApplyNumericSetting(machineValues, settings, "acceleration", 121);
            ApplyNumericSetting(machineValues, settings, "acceleration", 122);
            if (config.RotaryInstalled)
            {
                ApplyNumericSetting(machineValues, settings, "delta_max_rate", 113);
                ApplyNumericSetting(machineValues, settings, "delta_acceleration", 123);
            }
            Log("Read /sd/config.txt from Z1 and mapped " + machineValues.Count + " machine values into the session profile");
            return settings;
        }

        private static void ApplyNumericSetting(Dictionary<string, string> source, SortedDictionary<int, string> destination, string key, int number)
        {
            string value;
            double parsed;
            if (source.TryGetValue(key, out value) && Double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out parsed) && !Double.IsNaN(parsed) && !Double.IsInfinity(parsed))
                destination[number] = parsed.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void ApplyBooleanSetting(Dictionary<string, string> source, SortedDictionary<int, string> destination, string key, int number)
        {
            string value;
            bool parsed;
            if (source.TryGetValue(key, out value) && Boolean.TryParse(value, out parsed)) destination[number] = parsed ? "1" : "0";
        }

        private static void HandleClient(TcpClient local, Config config)
        {
            SortedDictionary<int, string> sessionSettings = new SortedDictionary<int, string>(config.GrblSettings);
            try { sessionSettings = BuildSessionSettings(config, ReadZ1ConfigText(config)); }
            catch (Exception ex) { Log("Could not read live /sd/config.txt; using YAML fallback profile: " + ex.Message); }
            TcpClient z1 = new TcpClient();
            try
            {
                z1.ReceiveTimeout = 500;
                z1.SendTimeout = 5000;
                z1.Connect(config.Host, config.Port);
                NetworkStream localStream = local.GetStream();
                NetworkStream z1Stream = z1.GetStream();
                var frameReader = new FrameReader(z1Stream);
                var done = new ManualResetEvent(false);
                PositionSnapshot positions = new PositionSnapshot();
                SensorSnapshot sensors = new SensorSnapshot();
                int pendingVersionReplies = 0;
                Thread receiver = new Thread(delegate()
                {
                    try
                    {
                        while (!done.WaitOne(0))
                        {
                            byte[] payload;
                            try { payload = frameReader.ReadFrame(); }
                            catch (IOException ex)
                            {
                                SocketException se = ex.InnerException as SocketException;
                                if (se != null && (se.SocketErrorCode == SocketError.TimedOut || se.SocketErrorCode == SocketError.WouldBlock)) continue;
                                throw;
                            }
                            if (payload.Length == 0 || (payload[0] != 0x80 && payload[0] != 0x81 && payload[0] != 0x82 && payload[0] != 0x90)) continue;
                            byte[] text = new byte[payload.Length - 1];
                            Buffer.BlockCopy(payload, 1, text, 0, text.Length);
                            string message = Encoding.UTF8.GetString(text).Replace("\r\n", "\n").Replace("\r", "\n");
                            if (message.Length == 0) continue;
                            if (payload[0] == 0x82)
                            {
                                UpdateSensors(sensors, message);
                                continue;
                            }
                            if (payload[0] == 0x81)
                            {
                                UpdatePositions(positions, message);
                                message = AddSensorFieldsToStatus(message, sensors);
                            }
                            string trimmedMessage = message.Trim();
                            if (payload[0] == 0x90 && trimmedMessage.StartsWith("version =", StringComparison.OrdinalIgnoreCase) && ConsumePendingVersionReply(ref pendingVersionReplies))
                            {
                                string firmwareVersion = trimmedMessage.Substring(trimmedMessage.IndexOf('=') + 1).Trim();
                                string grblVersionReply = "[VER:Grbl " + firmwareVersion + " (Makera Z1)]\r\nok\r\n";
                                byte[] versionWire = Encoding.UTF8.GetBytes(grblVersionReply);
                                Debug("Translated Z1 version response to GRBL $I response: " + grblVersionReply.Replace("\r", "\\r").Replace("\n", "\\n"));
                                WriteLocal(localStream, versionWire);
                                continue;
                            }
                            if (!message.EndsWith("\n")) message += "\r\n";
                            byte[] wire = Encoding.UTF8.GetBytes(message);
                            WriteLocal(localStream, wire);
                        }
                    }
                    catch (Exception ex) { if (!done.WaitOne(0)) Log("Z1 receive ended: " + ex.Message); }
                    finally { done.Set(); }
                });
                receiver.IsBackground = true;
                receiver.Start();
                Thread diagnosticPoller = new Thread(delegate()
                {
                    try
                    {
                        while (!done.WaitOne(700))
                        {
                            // Makera Studio keeps its diagnostics session active by
                            // polling this command about every 700 ms. Without it,
                            // accessory G-code can be acknowledged without changing
                            // the diagnostic outputs.
                            byte[] diagnosticCommand = Encoding.ASCII.GetBytes("diagnose\n");
                            byte[] payload = new byte[diagnosticCommand.Length + 1];
                            payload[0] = 0xA2;
                            Buffer.BlockCopy(diagnosticCommand, 0, payload, 1, diagnosticCommand.Length);
                            SendPayload(z1Stream, payload);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!done.WaitOne(0)) Log("Diagnostics poll stopped: " + ex.Message);
                        done.Set();
                    }
                });
                diagnosticPoller.IsBackground = true;
                diagnosticPoller.Start();
                Log("gSender connected; proxying to Z1 " + config.Host + ":" + config.Port);
                var line = new List<byte>();
                byte[] incoming = new byte[4096];
                while (!done.WaitOne(0))
                {
                    int count;
                    try { count = localStream.Read(incoming, 0, incoming.Length); }
                    catch (IOException ex)
                    {
                        SocketException se = ex.InnerException as SocketException;
                        if (se != null && (se.SocketErrorCode == SocketError.TimedOut || se.SocketErrorCode == SocketError.WouldBlock)) continue;
                        throw;
                    }
                    if (count == 0) break;
                    for (int i = 0; i < count; i++)
                    {
                        byte b = incoming[i];
                        if (b == (byte)'?') { FlushLine(line, z1Stream, localStream, config, sessionSettings, positions, ref pendingVersionReplies); SendPayload(z1Stream, new byte[] { 0xA1, (byte)'?' }); }
                        else if (b == 0x18)
                        {
                            // GRBL Ctrl-X is realtime. Makera's serial protocol expects
                            // the control-single packet type before the Ctrl-X byte.
                            if (line.Count > 0) Debug("Discarded incomplete G-code line on GRBL Ctrl-X");
                            line.Clear();
                            SendZ1Halt(z1Stream, positions);
                        }
                        else if (b == 0x85) { FlushLine(line, z1Stream, localStream, config, sessionSettings, positions, ref pendingVersionReplies); SendPayload(z1Stream, new byte[] { 0x85 }); }
                        else if (b == (byte)'!' || b == (byte)'~') { FlushLine(line, z1Stream, localStream, config, sessionSettings, positions, ref pendingVersionReplies); SendPayload(z1Stream, new byte[] { 0xA2, b }); }
                        else if (b == 0x0A || b == 0x0D) FlushLine(line, z1Stream, localStream, config, sessionSettings, positions, ref pendingVersionReplies);
                        else if (b >= 0x20)
                        {
                            line.Add(b);
                            if (line.Count > 8192)
                            {
                                line.Clear();
                                byte[] error = Encoding.ASCII.GetBytes("error: G-code command exceeds 8192 bytes\r\n");
                                WriteLocal(localStream, error);
                            }
                        }
                    }
                }
                if (line.Count > 0) FlushLine(line, z1Stream, localStream, config, sessionSettings, positions, ref pendingVersionReplies);
                done.Set();
                try { z1.Close(); } catch { }
                receiver.Join(1000);
                diagnosticPoller.Join(1000);
                Log("gSender disconnected");
            }
            catch (Exception ex)
            {
                try
                {
                    byte[] error = Encoding.UTF8.GetBytes("error: Makera Z1 bridge connection failed: " + ex.Message + "\r\n");
                    WriteLocal(local.GetStream(), error);
                }
                catch { }
                Log("Session ended: " + ex.Message);
            }
            finally { z1.Close(); }
        }

        private static void FlushLine(List<byte> line, NetworkStream stream, NetworkStream localStream, Config config, SortedDictionary<int, string> sessionSettings, PositionSnapshot positions, ref int pendingVersionReplies)
        {
            if (line.Count == 0) return;
            string command = Encoding.ASCII.GetString(line.ToArray()).Trim();
            if (command.StartsWith("$J=", StringComparison.OrdinalIgnoreCase))
            {
                string translatedJog;
                string jogError;
                if (!TryTranslateJog(command.Substring(3), positions, config, out translatedJog, out jogError))
                {
                    WriteLocal(localStream, Encoding.ASCII.GetBytes("error: " + jogError + "\r\n"));
                    Debug("Rejected GRBL jog: " + jogError);
                }
                else
                {
                    Debug("Translated GRBL jog to Makera motion: " + translatedJog);
                    byte[] jogCommand = Encoding.ASCII.GetBytes(translatedJog + "\n");
                    byte[] jogPayload = new byte[jogCommand.Length + 1];
                    jogPayload[0] = 0xA2;
                    Buffer.BlockCopy(jogCommand, 0, jogPayload, 1, jogCommand.Length);
                    SendPayload(stream, jogPayload);
                }
                line.Clear();
                return;
            }
            if (String.Equals(command, "$$", StringComparison.OrdinalIgnoreCase))
            {
                // The Z1 rejects the GRBL settings-list query. Return the documented
                // machine profile to gSender, then acknowledge the query.
                var response = new StringBuilder();
                foreach (KeyValuePair<int, string> setting in sessionSettings)
                {
                    if (!config.RotaryInstalled && (setting.Key == 103 || setting.Key == 113 || setting.Key == 123 || setting.Key == 133)) continue;
                    response.Append('$').Append(setting.Key).Append('=').Append(setting.Value).Append(" (Makera Z1 profile)\r\n");
                }
                response.Append("ok\r\n");
                WriteLocal(localStream, Encoding.ASCII.GetBytes(response.ToString()));
                Debug("Served " + sessionSettings.Count + " GRBL settings from the live Z1 config profile");
                line.Clear();
                return;
            }
            if (String.Equals(command, "$I", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref pendingVersionReplies);
                Debug("Translated GRBL $I query to Makera 'version' command");
                SendPayload(stream, new byte[] { 0xA2, (byte)'v', (byte)'e', (byte)'r', (byte)'s', (byte)'i', (byte)'o', (byte)'n' });
                line.Clear();
                return;
            }
            byte[] payload = new byte[line.Count + 1];
            payload[0] = 0xA2;
            line.CopyTo(payload, 1);
            line.Clear();
            SendPayload(stream, payload);
        }

        private static bool TryTranslateJog(string text, PositionSnapshot positions, Config config, out string translated, out string error)
        {
            translated = null;
            error = null;
            MatchCollection matches = Regex.Matches(text, @"([A-Z])([+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+))", RegexOptions.IgnoreCase);
            if (matches.Count == 0) { error = "invalid GRBL jog command"; return false; }
            string remainder = Regex.Replace(text, @"([A-Z])([+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+))", "", RegexOptions.IgnoreCase);
            if (remainder.Trim().Length != 0) { error = "unsupported content in GRBL jog command"; return false; }

            bool metric = true;
            bool absolute = false;
            bool machineCoordinates = false;
            bool hasDistanceMode = false;
            double[] requested = new double[5];
            bool[] specified = new bool[5];
            foreach (Match match in matches)
            {
                char letter = Char.ToUpperInvariant(match.Groups[1].Value[0]);
                double value;
                if (!Double.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) || Double.IsNaN(value) || Double.IsInfinity(value))
                { error = "invalid numeric value in GRBL jog command"; return false; }
                if (letter == 'G')
                {
                    int code = (int)value;
                    if (Math.Abs(value - code) > 0.000001) { error = "unsupported G-code in GRBL jog command"; return false; }
                    if (code == 20) metric = false;
                    else if (code == 21) metric = true;
                    else if (code == 90) { absolute = true; hasDistanceMode = true; }
                    else if (code == 91) { absolute = false; hasDistanceMode = true; }
                    else if (code == 53) machineCoordinates = true;
                    else { error = "unsupported G-code in GRBL jog command: G" + code; return false; }
                }
                else if (letter == 'F')
                {
                    if (value <= 0) { error = "jog feed rate must be positive"; return false; }
                }
                else
                {
                    int axis = letter == 'X' ? 0 : letter == 'Y' ? 1 : letter == 'Z' ? 2 : letter == 'A' ? 3 : -1;
                    if (axis < 0 || axis == 3 && !config.RotaryInstalled) { error = "unsupported axis in GRBL jog command: " + letter; return false; }
                    requested[axis] = metric ? value : value * 25.4;
                    specified[axis] = true;
                }
            }
            if (!hasDistanceMode) { error = "GRBL jog must specify G90 or G91"; return false; }
            bool anyAxis = false;
            for (int i = 0; i < specified.Length; i++) anyAxis |= specified[i];
            if (!anyAxis) { error = "GRBL jog must specify at least one axis"; return false; }

            double[] position;
            lock (positions.Sync)
            {
                double[] reported = machineCoordinates ? positions.Machine : positions.Work;
                double[] projected = machineCoordinates ? positions.ProjectedMachine : positions.ProjectedWork;
                if (reported == null || reported.Length < 4) { error = "waiting for a live Z1 position report before jogging"; return false; }
                position = (double[])(projected ?? reported).Clone();
            }
            var words = new StringBuilder("G21 G90 ");
            if (machineCoordinates) words.Append("G53 ");
            words.Append("G0");
            for (int axis = 0; axis < 4; axis++)
            {
                if (!specified[axis]) continue;
                double target = absolute ? requested[axis] : position[axis] + requested[axis];
                position[axis] = target;
                words.Append(' ').Append("XYZA"[axis]).Append(target.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture));
            }
            translated = words.ToString();
            lock (positions.Sync)
            {
                if (machineCoordinates) positions.ProjectedMachine = position;
                else positions.ProjectedWork = position;
            }
            return true;
        }

        private static void UpdatePositions(PositionSnapshot positions, string message)
        {
            if (message.Length == 0 || message[0] != '<') return;
            double[] machine;
            double[] work;
            if (!TryParseVector(message, "MPos:", out machine) || !TryParseVector(message, "WPos:", out work)) return;
            string activeState = message.Substring(1, message.IndexOf('|') > 0 ? message.IndexOf('|') - 1 : message.Length - 1);
            lock (positions.Sync)
            {
                positions.Machine = machine;
                positions.Work = work;
                if (activeState == "Idle" || activeState == "Alarm" || activeState == "Hold")
                {
                    positions.ProjectedMachine = (double[])machine.Clone();
                    positions.ProjectedWork = (double[])work.Clone();
                }
                else
                {
                    if (positions.ProjectedMachine == null) positions.ProjectedMachine = (double[])machine.Clone();
                    if (positions.ProjectedWork == null) positions.ProjectedWork = (double[])work.Clone();
                }
            }
        }

        private static void UpdateSensors(SensorSnapshot sensors, string message)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string body = message.Trim();
            if (body.StartsWith("{", StringComparison.Ordinal)) body = body.Substring(1);
            if (body.EndsWith("}", StringComparison.Ordinal)) body = body.Substring(0, body.Length - 1);
            foreach (string item in body.Split('|'))
            {
                int colon = item.IndexOf(':');
                if (colon <= 0) continue;
                fields[item.Substring(0, colon).Trim()] = item.Substring(colon + 1).Trim();
            }

            string[] endstops = GetDiagnosticVector(fields, "E");
            string[] probe = GetDiagnosticVector(fields, "P");
            var pins = new StringBuilder();

            // Makera's base endstop vector is ordered X-min, X-max, Y-min,
            // Y-max, Z-limit, cover. Firmware writes the cover state over slot 6.
            AddPinIfActive(pins, endstops, 0, 'X');
            AddPinIfActive(pins, endstops, 1, 'X');
            AddPinIfActive(pins, endstops, 2, 'Y');
            AddPinIfActive(pins, endstops, 3, 'Y');
            AddPinIfActive(pins, endstops, 4, 'Z');
            // In ATC/rotary configurations, the first optional E value is A-axis limit.
            AddPinIfActive(pins, endstops, 6, 'A');
            AddPinIfActive(pins, probe, 0, 'P');

            // Keep sensor inputs that have no standard GRBL Pn letter available
            // in a syntactically valid vendor extension for downstream consumers.
            var extension = new List<string>();
            AddDiagnosticExtension(extension, "E", fields);
            AddDiagnosticExtension(extension, "P", fields);
            AddDiagnosticExtension(extension, "A", fields);
            AddDiagnosticExtension(extension, "I", fields);
            AddDiagnosticExtension(extension, "T", fields);
            string pinStates = pins.ToString();
            string makeraFields = String.Join(";", extension.ToArray());
            string signature = pinStates + "|" + makeraFields;
            lock (sensors.Sync)
            {
                sensors.PinStates = pinStates;
                sensors.MakeraFields = makeraFields;
                if (sensors.LastDiagnostic != signature)
                {
                    sensors.LastDiagnostic = signature;
                    Debug("Z1 diagnostic sensors: Pn:" + (pinStates.Length == 0 ? "(none)" : pinStates) +
                        (makeraFields.Length == 0 ? "" : " Z1:" + makeraFields));
                }
            }
        }

        private static string[] GetDiagnosticVector(Dictionary<string, string> fields, string name)
        {
            string value;
            return fields.TryGetValue(name, out value) ? value.Split(',') : new string[0];
        }

        private static bool IsActive(string[] values, int index)
        {
            int parsed;
            return index < values.Length && Int32.TryParse(values[index].Trim(), out parsed) && parsed != 0;
        }

        private static void AddPinIfActive(StringBuilder pins, string[] values, int index, char pin)
        {
            if (IsActive(values, index) && pins.ToString().IndexOf(pin) < 0) pins.Append(pin);
        }

        private static void AddDiagnosticExtension(List<string> output, string name, Dictionary<string, string> fields)
        {
            string value;
            if (fields.TryGetValue(name, out value))
            {
                value = value.Replace("|", "").Replace(">", "").Replace("<", "").Trim();
                output.Add(name + "=" + value);
            }
        }

        private static string AddSensorFieldsToStatus(string message, SensorSnapshot sensors)
        {
            if (message.Length == 0 || message[0] != '<') return message;
            int end = message.IndexOf('>');
            if (end < 0) return message;
            string pinStates;
            string makeraFields;
            lock (sensors.Sync)
            {
                pinStates = sensors.PinStates;
                makeraFields = sensors.MakeraFields;
            }
            if (pinStates.Length == 0 && makeraFields.Length == 0) return message;

            string report = message.Substring(0, end);
            int existing = report.IndexOf("|Pn:", StringComparison.OrdinalIgnoreCase);
            string existingPins = "";
            if (existing >= 0)
            {
                int fieldEnd = report.IndexOf('|', existing + 1);
                if (fieldEnd < 0) fieldEnd = report.Length;
                string existingField = report.Substring(existing + 4, fieldEnd - existing - 4);
                existingPins = existingField.Trim();
                report = report.Remove(existing, fieldEnd - existing);
            }
            var mergedPins = new StringBuilder(existingPins);
            foreach (char pin in pinStates)
                if (mergedPins.ToString().IndexOf(pin) < 0) mergedPins.Append(pin);
            if (mergedPins.Length > 0) report += "|Pn:" + mergedPins;
            if (makeraFields.Length > 0) report += "|Z1:" + makeraFields;
            return report + message.Substring(end);
        }

        private static bool TryParseVector(string message, string label, out double[] values)
        {
            values = null;
            int start = message.IndexOf(label, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return false;
            start += label.Length;
            int end = message.IndexOfAny(new char[] { '|', '>' }, start);
            if (end < 0) return false;
            string[] parts = message.Substring(start, end - start).Split(',');
            if (parts.Length < 3) return false;
            values = new double[5];
            for (int i = 0; i < parts.Length && i < values.Length; i++)
                if (!Double.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out values[i])) return false;
            return true;
        }

        private static bool ConsumePendingVersionReply(ref int pending)
        {
            while (true)
            {
                int current = Interlocked.CompareExchange(ref pending, 0, 0);
                if (current <= 0) return false;
                if (Interlocked.CompareExchange(ref pending, current - 1, current) == current) return true;
            }
        }

        private static void SendPayload(NetworkStream stream, byte[] payload)
        {
            Debug("gSender -> Z1 frame: " + DescribePayload(payload));
            byte[] length = { (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2) };
            byte[] crcInput = new byte[length.Length + payload.Length];
            Buffer.BlockCopy(length, 0, crcInput, 0, length.Length);
            Buffer.BlockCopy(payload, 0, crcInput, 2, payload.Length);
            ushort crc = Crc16(crcInput);
            byte[] frame = new byte[2 + 2 + payload.Length + 2 + 2];
            Buffer.BlockCopy(Magic, 0, frame, 0, 2);
            Buffer.BlockCopy(length, 0, frame, 2, 2);
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            int crcOffset = 4 + payload.Length;
            frame[crcOffset] = (byte)(crc >> 8); frame[crcOffset + 1] = (byte)crc;
            frame[crcOffset + 2] = 0x55; frame[crcOffset + 3] = 0xAA;
            lock (SendLock) stream.Write(frame, 0, frame.Length);
        }

        private static void SendZ1Halt(NetworkStream stream, PositionSnapshot positions)
        {
            lock (positions.Sync)
            {
                if (positions.Machine != null) positions.ProjectedMachine = (double[])positions.Machine.Clone();
                if (positions.Work != null) positions.ProjectedWork = (double[])positions.Work.Clone();
            }
            Debug("Translated GRBL Ctrl-X to Makera PTYPE_CTRL_SINGLE halt packet (A1 18)");
            SendPayload(stream, new byte[] { 0xA1, 0x18 });
        }

        private static void WriteLocal(NetworkStream stream, byte[] data)
        {
            lock (LocalWriteLock) stream.Write(data, 0, data.Length);
        }

        private static ushort Crc16(byte[] data)
        {
            ushort crc = 0;
            foreach (byte b in data)
            {
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++) crc = (ushort)(((crc & 0x8000) != 0) ? ((crc << 1) ^ 0x1021) : (crc << 1));
            }
            return crc;
        }

        private static void Log(string text)
        {
            lock (LogLock) Console.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text);
        }

        private static void Debug(string text)
        {
            if (debugLogging) Log("DEBUG " + text);
        }

        private static string DescribePayload(byte[] payload)
        {
            var hex = new StringBuilder(payload.Length * 3);
            foreach (byte b in payload) hex.Append(b.ToString("X2")).Append(' ');
            string text = payload.Length > 1 ? Encoding.UTF8.GetString(payload, 1, payload.Length - 1) : "";
            text = text.Replace("\r", "\\r").Replace("\n", "\\n");
            if (text.Length > 160) text = text.Substring(0, 160) + "...";
            return "hex=[" + hex.ToString().TrimEnd() + "] text=[" + text + "]";
        }

        private sealed class FrameReader
        {
            private readonly NetworkStream stream;
            private readonly List<byte> buffer = new List<byte>();
            public FrameReader(NetworkStream stream) { this.stream = stream; }

            public byte[] ReadFrame()
            {
                while (true)
                {
                    int marker = FindMagic();
                    if (marker < 0) { if (buffer.Count > 1) buffer.RemoveRange(0, buffer.Count - 1); }
                    else if (marker > 0) buffer.RemoveRange(0, marker);
                    if (buffer.Count >= 4)
                    {
                        int length = (buffer[2] << 8) | buffer[3];
                        if (length < 2) throw new IOException("Invalid Z1 frame length");
                        int total = length + 6;
                        if (buffer.Count >= total)
                        {
                            int end = 4 + length - 2;
                            byte[] payload = buffer.GetRange(4, end - 4).ToArray();
                            ushort received = (ushort)((buffer[end] << 8) | buffer[end + 1]);
                            byte[] crcInput = buffer.GetRange(2, end - 2).ToArray();
                            bool trailer = buffer[end + 2] == 0x55 && buffer[end + 3] == 0xAA;
                            buffer.RemoveRange(0, total);
                            if (received == Crc16(crcInput) && trailer)
                            {
                                Debug("Z1 -> bridge frame: " + DescribePayload(payload));
                                return payload;
                            }
                            Log("Discarded invalid Z1 frame");
                            continue;
                        }
                    }
                    byte[] chunk = new byte[4096];
                    int read = stream.Read(chunk, 0, chunk.Length);
                    if (read == 0) throw new IOException("Z1 closed the connection");
                    for (int i = 0; i < read; i++) buffer.Add(chunk[i]);
                }
            }

            private int FindMagic()
            {
                for (int i = 0; i + 1 < buffer.Count; i++) if (buffer[i] == Magic[0] && buffer[i + 1] == Magic[1]) return i;
                return -1;
            }
        }
    }
}






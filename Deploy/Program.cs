using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Deploy
{
    class Program
    {
        static void Main(string[] args)
        {
            var secretFiles = new List<string>();
            var composeFile = "docker-compose.yml";
            try
            {
                var inputFile = args[0];
                String json;
                using (var stream = new StreamReader(File.Open(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read)))
                {
                    json = stream.ReadToEnd();
                }
                dynamic parsed = JsonConvert.DeserializeObject<ExpandoObject>(json);

                //Get stack name
                var stack = parsed.stack;
                ((IDictionary<String, dynamic>)parsed).Remove("stack");

                //Remove secrets
                using (var md5 = MD5.Create())
                {
                    ExpandoObject newSecrets = new ExpandoObject();
                    foreach (var secret in ((IDictionary<String, dynamic>)parsed.secrets))
                    {
                        if (secret.Value as String == "external")
                        {
                            //Setup default secret, which is external
                            newSecrets.TryAdd(secret.Key, new
                            {
                                external = true
                            });
                        }
                        else
                        {
                            //pull out secrets, put in file and then update secret entry
                            String secretJson = JsonConvert.SerializeObject(secret.Value);
                            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(secretJson));

                            // Convert to hex string
                            StringBuilder sb = new StringBuilder();
                            for (int i = 0; i < hash.Length; i++)
                            {
                                sb.Append(hash[i].ToString("X2"));
                            }
                            var hashStr = sb.ToString();

                            var file = secret.Key;
                            using (var secretStream = new StreamWriter(File.Open(file, FileMode.Create)))
                            {
                                secretStream.Write(secretJson);
                            }
                            secretFiles.Add(file);

                            newSecrets.TryAdd(secret.Key, new
                            {
                                file = file,
                                name = $"{stack}_s_{hashStr}"
                            });
                        }
                    }
                    parsed.secrets = newSecrets;
                }

                //Go through images and figure out specifics
                foreach (var service in ((IDictionary<String, dynamic>)parsed.services))
                {
                    //Figure out os deployment
                    var image = service.Value.image;

                    var split = image.Split('-');
                    if (split.Length != 3)
                    {
                        throw new InvalidOperationException("Incorrect image format. Image must be in the format registry/image-os-arch");
                    }
                    var os = split[split.Length - 2];
                    String pathRoot = null;
                    switch (os)
                    {
                        case "windows":
                            pathRoot = "c:/";
                            break;
                        case "linux":
                            pathRoot = "/";
                            break;
                        default:
                            throw new InvalidOperationException($"Invalid os '{os}', must be 'windows' or 'linux'");
                    }

                    //Ensure node exists
                    ((ExpandoObject)service.Value).TryAdd("deploy", new ExpandoObject());
                    ((ExpandoObject)service.Value.deploy).TryAdd("placement", new ExpandoObject());
                    ((ExpandoObject)service.Value.deploy.placement).TryAdd("constraints", new List<Object>());

                    service.Value.deploy.placement.constraints.Add($"node.platform.os == {os}");

                    //Transform secrets that are rooted with ~:/
                    foreach (var secret in service.Value.secrets)
                    {
                        if (secret.target.StartsWith("~:/"))
                        {
                            secret.target = pathRoot + secret.target.Substring(3);
                        }
                    }

                    //Transform volumes that are rooted with ~:/
                    foreach (var volume in service.Value.volumes)
                    {
                        if (volume.target.StartsWith("~:/"))
                        {
                            volume.target = pathRoot + volume.target.Substring(3);
                        }
                        if(!((IDictionary<String, dynamic>)volume).ContainsKey("type") || volume.type == null)
                        {
                            volume.type = "volume";
                        }
                    }
                }

                var serializer = new YamlDotNet.Serialization.Serializer();
                var yaml = serializer.Serialize(parsed);
                using (var outStream = new StreamWriter(File.Open(composeFile, FileMode.Create, FileAccess.Write, FileShare.None)))
                {
                    outStream.WriteLine("version: '3.5'");
                    outStream.Write(yaml);
                }

                //Run deployment
                var startInfo = new ProcessStartInfo("docker", $"stack deploy --prune --with-registry-auth -c {composeFile} {stack}")
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using (var deployCommand = Process.Start(startInfo))
                {
                    deployCommand.ErrorDataReceived += (s, e) =>
                    {
                        Console.Error.WriteLine(e.Data);
                    };
                    deployCommand.OutputDataReceived += (s, e) =>
                    {
                        Console.WriteLine(e.Data);
                    };
                    deployCommand.BeginErrorReadLine();
                    deployCommand.BeginOutputReadLine();

                    deployCommand.WaitForExit();
                }
            }
            finally
            {
                foreach (var secretFile in secretFiles.Concat(new String[] { composeFile }))
                {
                    try
                    {
                        File.Delete(secretFile);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"{ex.GetType().Name} when deleting {secretFile}. Will try to erase the rest of the files.");
                    }
                }
            }
        }

        private static void Process_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            throw new NotImplementedException();
        }

        private static void Process_ErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            throw new NotImplementedException();
        }
    }
}

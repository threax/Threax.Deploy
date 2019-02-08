using Docker.DotNet;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace Deploy
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var host = "unix:///var/run/docker.sock";

            using (var config = new DockerClientConfiguration(new Uri(host)))
            using (var client = config.CreateClient())
            {
                var filesToDelete = new List<string>();
                String registry = null;
                String registryUser = null;
                String registryPass = null;
                bool verbose = false;
                String inputFile = "docker-compose.json";
                try
                {
                    for (var i = 0; i < args.Length; ++i)
                    {
                        switch (args[i])
                        {
                            case "-c":
                                inputFile = Path.GetFullPath(args[++i]);
                                break;
                            case "-reg":
                                registry = args[++i];
                                break;
                            case "-user":
                                registryUser = args[++i];
                                break;
                            case "-pass":
                                registryPass = args[++i];
                                break;
                            case "-v":
                                verbose = true;
                                break;
                            case "--help":
                                Console.WriteLine("Threax.Deploy run with:");
                                Console.WriteLine("dotnet Deploy.dll options");
                                Console.WriteLine();
                                Console.WriteLine("options can be as follows:");
                                Console.WriteLine("-c - The compose file to load. Defaults to docker-compose.json in the current directory.");
                                Console.WriteLine("-v - Run in verbose mode, which will echo the final yml file.");
                                Console.WriteLine("-reg - The name of a remote registry to log into.");
                                Console.WriteLine("-user - The username for the remote registry.");
                                Console.WriteLine("-pass - The password for the remote registry.");
                                return; //End program
                            default:
                                Console.WriteLine($"Unknown argument {args[i]}");
                                return;
                        }
                    }

                    //See if we need to login
                    if (registry != null)
                    {
                        if (registryUser == null || registryPass == null)
                        {
                            Console.WriteLine("You must provide a -user and -pass when using a registry.");
                            return;
                        }
                        RunProcessWithOutput(new ProcessStartInfo("docker", $"login -u {registryUser} -p {registryPass} {registry}"));
                    }

                    String json;
                    using (var stream = new StreamReader(File.Open(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read)))
                    {
                        json = stream.ReadToEnd();
                    }

                    var outBasePath = Path.GetDirectoryName(inputFile);

                    IDictionary<String, dynamic> parsed = JsonConvert.DeserializeObject<ExpandoObject>(json);

                    //Get stack name
                    var stack = parsed["stack"];
                    parsed.Remove("stack");

                    //Remove secrets
                    ExpandoObject newSecrets = new ExpandoObject(); //These are used below if ssl certs are added
                    using (var md5 = MD5.Create())
                    {
                        foreach (KeyValuePair<String, dynamic> secret in parsed["secrets"])
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

                                var file = Path.Combine(outBasePath, secret.Key);
                                using (var secretStream = new StreamWriter(File.Open(file, FileMode.Create)))
                                {
                                    secretStream.Write(secretJson);
                                }
                                filesToDelete.Add(file);

                                newSecrets.TryAdd(secret.Key, new
                                {
                                    file = file,
                                    name = $"{stack}_s_{hashStr}"
                                });
                            }
                        }
                        parsed["secrets"] = newSecrets;
                    }

                    //Go through images and figure out specifics
                    foreach (KeyValuePair<String, dynamic> service in parsed["services"])
                    {
                        IDictionary<String, dynamic> serviceValue = service.Value;

                        //Figure out os deployment
                        var image = serviceValue["image"];

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
                        ((ExpandoObject)serviceValue).TryAdd("deploy", new ExpandoObject());
                        ((ExpandoObject)serviceValue["deploy"]).TryAdd("placement", new ExpandoObject());
                        ((ExpandoObject)((IDictionary<String, dynamic>)serviceValue["deploy"])["placement"]).TryAdd("constraints", new List<Object>());
                        var constraints = ((IDictionary<String, dynamic>)((IDictionary<String, dynamic>)serviceValue["deploy"])["placement"])["constraints"];
                        //((ExpandoObject)serviceValue["deploy"]["placement"]).TryAdd

                        constraints.Add($"node.platform.os == {os}");

                        //Transform volumes that are rooted with ~:/
                        if (serviceValue.TryGetValue("volumes", out var volumes))
                        {
                            foreach (IDictionary<String, dynamic> volume in volumes)
                            {
                                if (volume["target"].StartsWith("~:/"))
                                {
                                    volume["target"] = pathRoot + volume["target"].Substring(3);
                                }
                            }
                        }

                        //Generate certs for any labels requesting it
                        if (serviceValue.TryGetValue("labels", out var labels))
                        {
                            var count = ((IList<Object>)labels).Count;
                            for (var i = 0; i < count; ++i)
                            {
                                if (labels[i].Contains("{{Threax.StackDeploy.CreateCert()}}"))
                                {
                                    var swarmSecrets = await client.Secrets.ListAsync();
                                    Console.Write(JsonConvert.SerializeObject(swarmSecrets, Formatting.Indented));
                                    var secretName = $"{stack}_{service.Key}_ssl";
                                    if (swarmSecrets.Any(s =>
                                    {
                                        if(s.Spec.Labels.TryGetValue("com.docker.stack.namespace", out var stackNamespace))
                                        {
                                            return stackNamespace == stack && s.Spec.Name == secretName;
                                        }
                                        return false;
                                    }))
                                    {
                                        newSecrets.TryAdd($"{service.Key}-ssl", new
                                        {
                                            name = secretName,
                                            external = true
                                        });
                                    }
                                    else
                                    {
                                        var certFile = Path.Combine(outBasePath, service.Key + "Private.pfx");
                                        var cert = CreateCerts(certFile);
                                        filesToDelete.Add(certFile);

                                        newSecrets.TryAdd($"{service.Key}-ssl", new
                                        {
                                            file = certFile,
                                            name = secretName
                                        });

                                        labels[i] = labels[i].Replace("{{Threax.StackDeploy.CreateCert()}}", cert);
                                    }
                                }
                            }
                        }

                        //Transform secrets that are rooted with ~:/
                        if (serviceValue.TryGetValue("secrets", out var secrets))
                        {
                            foreach (IDictionary<String, dynamic> secret in secrets)
                            {
                                if (secret["target"].StartsWith("~:/"))
                                {
                                    secret["target"] = pathRoot + secret["target"].Substring(3);
                                }
                            }
                        }
                    }

                    var composeFile = Path.Combine(outBasePath, "docker-compose.yml");
                    filesToDelete.Add(composeFile);
                    var serializer = new YamlDotNet.Serialization.Serializer();
                    var yaml = serializer.Serialize(parsed);
                    if (verbose)
                    {
                        Console.WriteLine(yaml);
                    }
                    using (var outStream = new StreamWriter(File.Open(composeFile, FileMode.Create, FileAccess.Write, FileShare.None)))
                    {
                        outStream.WriteLine("version: '3.5'");
                        outStream.Write(yaml);
                    }

                    //Run deployment
                    RunProcessWithOutput(new ProcessStartInfo("docker", $"stack deploy --prune --with-registry-auth -c {composeFile} {stack}"));
                }
                finally
                {
                    foreach (var secretFile in filesToDelete)
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

                    if (registry != null)
                    {
                        RunProcessWithOutput(new ProcessStartInfo("docker", $"logout {registry}"));
                    }
                }
            }
        }

        private static void RunProcessWithOutput(ProcessStartInfo startInfo)
        {
            startInfo.RedirectStandardError = true;
            startInfo.RedirectStandardOutput = true;
            using (var process = Process.Start(startInfo))
            {
                process.ErrorDataReceived += (s, e) =>
                {
                    Console.Error.WriteLine(e.Data);
                };
                process.OutputDataReceived += (s, e) =>
                {
                    Console.WriteLine(e.Data);
                };
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                process.WaitForExit();
            }
        }

        /// <summary>
        /// Create certs. The public key is saved in pfx format to privateKeyFile and the public cert is returned
        /// from this function CER encoded.
        /// </summary>
        /// <param name="privateKeyFile"></param>
        /// <returns></returns>
        private static String CreateCerts(String privateKeyFile)
        {
            using (var rsa = RSA.Create()) // generate asymmetric key pair
            {
                var request = new CertificateRequest($"cn=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                //Thanks to Muscicapa Striata for these settings at
                //https://stackoverflow.com/questions/42786986/how-to-create-a-valid-self-signed-x509certificate2-programmatically-not-loadin
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DataEncipherment | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature, false));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

                //Create the cert
                var certificate = request.CreateSelfSigned(new DateTimeOffset(DateTime.UtcNow.AddMinutes(-1)), new DateTimeOffset(DateTime.UtcNow.AddYears(5)));

                // Create pfx with private key
                File.WriteAllBytes(privateKeyFile, certificate.Export(X509ContentType.Pfx));

                // Create Base 64 encoded CER public key only
                return
                "-----BEGIN CERTIFICATE-----"
                + Convert.ToBase64String(certificate.Export(X509ContentType.Cert), Base64FormattingOptions.None)
                + "-----END CERTIFICATE-----";
            }
        }
    }
}

using Docker.DotNet;
using LibGit2Sharp;
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
                String repoUser = null;
                String repoPass = null;
                String registry = null;
                String registryUser = null;
                String registryPass = null;
                bool verbose = false;
                bool deleteFiles = true;
                bool buildImages = false;
                bool autoTag = true;
                bool deploy = true;
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
                            case "-repouser":
                                repoUser = args[++i];
                                break;
                            case "-repopass":
                                repoPass = args[++i];
                                break;
                            case "-reg":
                                registry = args[++i];
                                break;
                            case "-reguser":
                                registryUser = args[++i];
                                break;
                            case "-regpass":
                                registryPass = args[++i];
                                break;
                            case "-v":
                                verbose = true;
                                break;
                            case "-keep":
                                deleteFiles = false;
                                break;
                            case "-build":
                                buildImages = true;
                                break;
                            case "-noAutoTag":
                                autoTag = false;
                                break;
                            case "-nodeploy":
                                deploy = false;
                                break;
                            case "--help":
                                Console.WriteLine("Threax.Deploy run with:");
                                Console.WriteLine("dotnet Deploy.dll options");
                                Console.WriteLine();
                                Console.WriteLine("options can be as follows:");
                                Console.WriteLine("-c - The compose file to load. Defaults to docker-compose.json in the current directory.");
                                Console.WriteLine("-v - Run in verbose mode, which will echo the final yml file.");
                                Console.WriteLine("-repouser - The username for the git repo.");
                                Console.WriteLine("-repopass - The password for the git repo.");
                                Console.WriteLine("-reg - The name of a remote registry to log into.");
                                Console.WriteLine("-reguser - The username for the remote registry.");
                                Console.WriteLine("-regpass - The password for the remote registry.");
                                Console.WriteLine("-keep - Don't erase output files. Will keep secrets, use carefully.");
                                Console.WriteLine("-build - Build images before deployment.");
                                Console.WriteLine("-nodeploy - Don't deploy images. Can use -build -nodeploy to just build images.");
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
                            Console.WriteLine("You must provide a -reguser and -regpass when using a registry.");
                            return;
                        }
                        RunProcessWithOutput(new ProcessStartInfo("docker", $"login -u {registryUser} -p {registryPass} {registry}"));
                    }

                    var inputFilePath = Path.GetDirectoryName(inputFile);
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

                    IDictionary<String, dynamic> newFileSecrets = new ExpandoObject(); //These are used below if ssl certs are added

                    //Go through images and figure out specifics
                    foreach (KeyValuePair<String, dynamic> service in parsed["services"])
                    {
                        IDictionary<String, dynamic> serviceValue = service.Value;

                        //Figure out os deployment
                        var image = serviceValue["image"];

                        var split = image.Split('-');
                        if (split.Length < 3)
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

                        //If build is set, attempt to build the image
                        if (serviceValue.TryGetValue("build", out dynamic buildInfo))
                        {
                            IDictionary<String, dynamic> buildInfoDict = buildInfo;
                            serviceValue.Remove("build");
                            if (buildImages)
                            {
                                var tag = image;
                                if (autoTag)
                                {
                                    var now = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                                    tag += ":" + now;
                                }

                                if(!buildInfoDict.TryGetValue("context", out dynamic context))
                                {
                                    context = ".";
                                }
                                context = Path.GetFullPath(Path.Combine(outBasePath, context));

                                //See if we should clone or pull a git repo
                                if (buildInfoDict.TryGetValue("repo", out var repo))
                                {
                                    CloneGitRepo(repo, repoUser, repoPass, context);
                                }

                                if (!buildInfoDict.TryGetValue("dockerfile", out dynamic dockerFile))
                                {
                                    dockerFile = "Dockerfile";
                                }
                                dockerFile = Path.Combine(context, dockerFile);

                                Console.WriteLine($"Building image {image} from {context} with dockerfile {dockerFile}. Taging with {tag} and {image}:latest");
                                RunProcessWithOutput(new ProcessStartInfo("docker", $"build -f {dockerFile} -t {tag} -t {image}:latest {context}"));

                                //Since we have a tag, update image in the yml
                                serviceValue["image"] = tag;
                            }
                        }

                        //Ensure node exists
                        ((ExpandoObject)serviceValue).TryAdd("deploy", new ExpandoObject());
                        ((ExpandoObject)serviceValue["deploy"]).TryAdd("placement", new ExpandoObject());
                        ((ExpandoObject)((IDictionary<String, dynamic>)serviceValue["deploy"])["placement"]).TryAdd("constraints", new List<Object>());
                        var constraints = ((IDictionary<String, dynamic>)((IDictionary<String, dynamic>)serviceValue["deploy"])["placement"])["constraints"];

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

                        //Handle extensions
                        if (deploy && serviceValue.TryGetValue("ext", out var ext))
                        {
                            serviceValue.Remove("ext");
                            var extDic = (IDictionary<String, dynamic>)ext;
                            if (extDic.TryGetValue("genssl", out dynamic genssl))
                            {
                                var gensslDic = (IDictionary<String, dynamic>)genssl;
                                if (!gensslDic.TryGetValue("target", out dynamic target))
                                {
                                    throw new InvalidOperationException("A genssl object must have a target entry.");
                                }
                                var sslSecretKey = $"auto_ssl_{stack}_{service.Key}";

                                var swarmSecrets = await client.Secrets.ListAsync();
                                var secretName = $"{stack}_{service.Key}_ssl";
                                if (swarmSecrets.Any(s =>
                                {
                                    if (s.Spec.Labels.TryGetValue("com.docker.stack.namespace", out var stackNamespace))
                                    {
                                        return stackNamespace == stack && s.Spec.Name == secretName;
                                    }
                                    return false;
                                }))
                                {
                                    //If there is already a secret, use that
                                    Console.WriteLine($"Found exising ssl secret for {stack}_{service.Key}. Using cert from existing service.");

                                    newFileSecrets[sslSecretKey] = new
                                    {
                                        name = secretName,
                                        external = true
                                    };
                                }
                                else
                                {
                                    //Create a new secret
                                    Console.WriteLine($"No exising ssl secret for {stack} {service.Key}. Creating a new one.");

                                    var certFile = Path.Combine(outBasePath, service.Key + "Private.pfx");
                                    CreateCerts(certFile, secretName);
                                    filesToDelete.Add(certFile);

                                    newFileSecrets[sslSecretKey] = new
                                    {
                                        name = secretName,
                                        file = certFile
                                    };
                                }

                                //Add secret to service's secret section
                                if (!serviceValue.TryGetValue("secrets", out var serviceSecrets))
                                {
                                    serviceSecrets = new List<dynamic>();
                                    serviceValue.Add("secrets", serviceSecrets);
                                }

                                serviceSecrets.Add(new Dictionary<String, dynamic>() { { "source", sslSecretKey}, { "target", target } });
                            }
                        }

                        //Transform secrets that are rooted with ~:/
                        if (serviceValue.TryGetValue("secrets", out var secrets))
                        {
                            foreach (dynamic dySec in secrets)
                            {
                                var secret = dySec as IDictionary<String, dynamic>;
                                if (secret != null)
                                {
                                    if (secret["target"].StartsWith("~:/"))
                                    {
                                        secret["target"] = pathRoot + secret["target"].Substring(3);
                                    }
                                }
                            }
                        }
                    }

                    //Remove secrets
                    using (var md5 = MD5.Create())
                    {
                        foreach (KeyValuePair<String, dynamic> secret in parsed["secrets"])
                        {
                            var secretString = secret.Value as String;
                            if (secretString == "external")
                            {
                                //Setup default secret, which is external
                                newFileSecrets.TryAdd(secret.Key, new
                                {
                                    external = true
                                });
                            }
                            else if(secretString != null)
                            {
                                var secretFilePath = Path.Combine(inputFilePath, secretString);
                                //This is probably a file
                                if (File.Exists(secretFilePath))
                                {
                                    byte[] hash;
                                    using(var stream = File.Open(secretFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                                    {
                                        hash = md5.ComputeHash(stream);
                                    }
                                    var hashStr = HashToHex(hash);

                                    newFileSecrets.TryAdd(secret.Key, new
                                    {
                                        file = secretFilePath,
                                        name = $"{stack}_s_{hashStr}"
                                    });
                                }
                                else
                                {
                                    Console.WriteLine($"Cannot find file {secretFilePath}");
                                }
                            }
                            else
                            {
                                //pull out secrets, put in file and then update secret entry
                                String secretJson = JsonConvert.SerializeObject(secret.Value);
                                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(secretJson));
                                string hashStr = HashToHex(hash);

                                var file = Path.Combine(outBasePath, secret.Key);
                                using (var secretStream = new StreamWriter(File.Open(file, FileMode.Create)))
                                {
                                    secretStream.Write(secretJson);
                                }
                                filesToDelete.Add(file);

                                newFileSecrets.TryAdd(secret.Key, new
                                {
                                    file = file,
                                    name = $"{stack}_s_{hashStr}"
                                });
                            }
                        }
                        parsed["secrets"] = newFileSecrets;
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
                    if (deploy)
                    {
                        RunProcessWithOutput(new ProcessStartInfo("docker", $"stack deploy --prune --with-registry-auth -c {composeFile} {stack}"));
                    }
                }
                catch(Exception ex)
                {
                    Console.WriteLine($"{ex.GetType().Name} occured. Message: {ex.Message}");
                }
                finally
                {
                    if (deleteFiles)
                    {
                        foreach (var secretFile in filesToDelete)
                        {
                            try
                            {
                                if (verbose)
                                {
                                    Console.WriteLine($"Cleanup {secretFile}");
                                }
                                File.Delete(secretFile);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"{ex.GetType().Name} when deleting {secretFile}. Will try to erase the rest of the files.");
                            }
                        }
                    }

                    if (registry != null)
                    {
                        RunProcessWithOutput(new ProcessStartInfo("docker", $"logout {registry}"));
                    }
                }
            }
        }

        private static string HashToHex(byte[] hash)
        {
            // Convert to hex string
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < hash.Length; i++)
            {
                sb.Append(hash[i].ToString("X2"));
            }
            var hashStr = sb.ToString();
            return hashStr;
        }

        private static void CloneGitRepo(string repo, string repoUser, string repoPass, dynamic context)
        {
            if (Directory.Exists(context))
            {
                Console.WriteLine($"Pulling changes to {context}");
                var path = Path.Combine(context, ".git");
                using (var gitRepository = new LibGit2Sharp.Repository(path))
                {
                    var signature = new Signature("bot", "bot@bot", DateTime.Now);
                    var result = Commands.Pull(gitRepository, signature, new PullOptions()
                    {
                        FetchOptions = new FetchOptions()
                        {
                            CredentialsProvider = (u, user, cred) => new UsernamePasswordCredentials()
                            {
                                Username = repoUser,
                                Password = repoPass
                            }
                        }
                    });
                }
            }
            else
            {
                Console.WriteLine($"Cloning {repo} to {context}");
                LibGit2Sharp.Repository.Clone(repo, context, new CloneOptions()
                {
                    CredentialsProvider = (u, user, cred) => new UsernamePasswordCredentials()
                    {
                        Username = repoUser,
                        Password = repoPass
                    }
                });
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
                    if (!String.IsNullOrEmpty(e.Data))
                    {
                        Console.Error.WriteLine(e.Data);
                    }
                };
                process.OutputDataReceived += (s, e) =>
                {
                    if (!String.IsNullOrEmpty(e.Data))
                    {
                        Console.WriteLine(e.Data);
                    }
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
        private static void CreateCerts(String privateKeyFile, String cn)
        {
            using (var rsa = RSA.Create()) // generate asymmetric key pair
            {
                var request = new CertificateRequest($"cn={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                //Thanks to Muscicapa Striata for these settings at
                //https://stackoverflow.com/questions/42786986/how-to-create-a-valid-self-signed-x509certificate2-programmatically-not-loadin
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DataEncipherment | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature, false));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

                //Create the cert
                var certificate = request.CreateSelfSigned(new DateTimeOffset(DateTime.UtcNow.AddMinutes(-1)), new DateTimeOffset(DateTime.UtcNow.AddYears(25)));

                // Create pfx with private key
                File.WriteAllBytes(privateKeyFile, certificate.Export(X509ContentType.Pfx));
            }
        }
    }
}

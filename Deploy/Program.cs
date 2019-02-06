using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Deploy
{
    class Program
    {
        static void Main(string[] args)
        {
            var secretFiles = new List<string>();
            String json;
            using (var stream = new StreamReader(File.Open("docker-compose.json", FileMode.Open, FileAccess.Read, FileShare.Read)))
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
            foreach(var service in ((IDictionary<String, dynamic>)parsed.services))
            {
                var image = service.Value.image;

                var split = image.Split('-');
                if (split.Length != 3)
                {
                    throw new InvalidOperationException("Incorrect image format. Image must be in the format registry/image-os-arch");
                }
                var os = split[split.Length - 2];
                if (os != "windows" && os != "linux")
                {
                    throw new InvalidOperationException($"Invalid os '{os}', must be 'windows' or 'linux'");
                }

                //Ensure node exists
                ((ExpandoObject)service.Value).TryAdd("deploy", new ExpandoObject());
                ((ExpandoObject)service.Value.deploy).TryAdd("placement", new ExpandoObject());
                ((ExpandoObject)service.Value.deploy.placement).TryAdd("constraints", new List<Object>());

                service.Value.deploy.placement.constraints.Add($"node.platform.os == {os}");
            }

            var serializer = new YamlDotNet.Serialization.Serializer();
            var yaml = serializer.Serialize(parsed);
            using (var outStream = new StreamWriter(File.Open("docker-compose.yml", FileMode.Create, FileAccess.Write, FileShare.None)))
            {
                outStream.Write(yaml);
            }
        }
    }
}

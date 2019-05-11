# Threax.StackDeploy
This program wraps up docker stack deploy with a couple extra features:

* A way to specify rooted paths that work on both Linux and Windows.
* A way to define secrets right in the deployment file.
* Allow the use of json to describe the docker deployment, converted to yaml when the command runs.
* Log in and out of a remote registry during deployment.

This smooths out some of the issues I had deploying to a mixed mode Docker Swarm.

You can run it like follows:
```
docker run -it --rm -v /var/run/docker.sock:/var/run/docker.sock -v $(pwd):/data threax/stack-deploy
```
This will find a file in the current directory named docker-compose.json and deploy it.

You can use `${PWD}` instead of `$(pwd)` on Windows.

If you want you can specify the following arguments:
[-c filname] [-l] [-reg registry] [-user registry user] [-pass registry password]
filename - required - The name of the file to read.
-c - The docker-compose.json file to load.
-v - Run in verbose mode and output the final yml file.
-reg - The name of a remote registry to log into.
-user - The username for the remote registry.
-pass - The password for the remote registry.
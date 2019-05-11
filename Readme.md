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
* -c - The compose file to load. Defaults to docker-compose.json in the current directory.
* -v - Run in verbose mode, which will echo the final yml file.
* -reg - The name of a remote registry to log into.
* -reguser - The username for the remote registry.
* -regpass - The password for the remote registry.
* -keep - Don't erase output files. Will keep secrets, use carefully.
* -build - Build images before deployment.
* -nodeploy - Don't deploy images. Can use -build -nodeploy to just build images.

Put the arguments at the end of the command above.
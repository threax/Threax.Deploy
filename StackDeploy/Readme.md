# Threax.StackDeploy
This program wraps up docker stack deploy with a couple extra features:

* A way to specify rooted paths that work on both Linux and Windows.
* A way to define secrets right in the deployment file.
* Allow the use of json to describe the docker deployment, converted to yaml when the command runs.

This smooths out some of the issues we had deploying to a mixed mode Docker Swarm.

You can run it like follows:
```
docker run -it --rm -v /var/run/docker.sock:/var/run/docker.sock -v $(pwd):/data threax/stack-deploy
```
This will find a file in the current directory named docker-compose.json and deploy it.

You can use `${PWD}` instead of `$(pwd)` on Windows.

If you want you can specify the following arguments:
filname [-l]
filenam - required - The name of the file to read.
-l - optional - Change the current directory to the directory the docker-compose.json file comes from.
/data/docker-compose.json -l
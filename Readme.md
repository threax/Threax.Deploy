# Threax.StackDeploy
This program wraps up docker stack deploy with a couple extra features:

* A way to specify rooted paths that work on both Linux and Windows.
* A way to define secrets right in the deployment file.
* Allow the use of json to describe the docker deployment, converted to yaml when the command runs.
* Log in and out of a remote registry during deployment.
* Clone a git repo by specifying it in the build section.

This smooths out some of the issues I had deploying to a mixed mode Docker Swarm.

## Running Image
You can run it like follows:
```
docker run -it --rm -v /var/run/docker.sock:/var/run/docker.sock -v $(pwd):/data threax/stack-deploy
```
This will find a file in the current directory named docker-compose.json and deploy it.

You can use `${PWD}` instead of `$(pwd)` on Windows.

If you want you can specify the following arguments:
* -c - The compose file to load. Defaults to docker-compose.json in the current directory.
* -v - Run in verbose mode, which will echo the final yml file.
* -repouser - The username for the git repo.
* -repopass - The password for the git repo.
* -reg - The name of a remote registry to log into.
* -reguser - The username for the remote registry.
* -regpass - The password for the remote registry.
* -keep - Don't erase output files. Will keep secrets, use carefully.
* -build - Build images before deployment.
* -nodeploy - Don't deploy images. Can use -build -nodeploy to just build images.

Put the arguments at the end of the command above.

## Building Image
To build the image go to the StackDeploy folder and then run:
```
docker build -t threax/stack-deploy .
```

Replace or remove version with the tagged version.

## Testing Image
The image can be tested with the TestApp folder. This will build a test deployment using the Threax.AspNetCore.Template project.

Run the following command after building the image in the TestApp folder to test it.
```
docker run -it --rm -v /var/run/docker.sock:/var/run/docker.sock -v ${PWD}:/data threax/stack-deploy -v -build -nodeploy
```
This will run the stack deploy, which will clone the repo and build the image. The -nodeploy argument will prevent it from running.
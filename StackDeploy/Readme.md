# Threax.StackDeploy
This program wraps up docker stack deploy with a couple extra features:

* A way to specify rooted paths that work on both Linux and Windows.
* A way to define secrets right in the deployment file.
* Allow the use of json to describe the docker deployment, converted to yaml when the command runs.

This smooths out some of the issues we had deploying to a mixed mode Docker Swarm.
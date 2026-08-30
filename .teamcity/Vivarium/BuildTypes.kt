package Vivarium

import jetbrains.buildServer.configs.kotlin.*
import jetbrains.buildServer.configs.kotlin.buildFeatures.XmlReport
import jetbrains.buildServer.configs.kotlin.buildFeatures.xmlReport
import jetbrains.buildServer.configs.kotlin.buildSteps.exec
import jetbrains.buildServer.configs.kotlin.triggers.vcs

private const val DotNet10Parameter = "DotNetCoreSDK10.0_Path"
private const val BuildVersionParameter = "VivariumBuildVersion"
private const val DockerImageParameter = "VivariumDockerImage"
private const val DockerImage = "registry.eyeauras.net:5000/ixab3r/viv-server"

private fun BuildType.commonVcs() {
    vcs {
        root(VivariumVcs)
        checkoutMode = CheckoutMode.ON_SERVER
        cleanCheckout = true
    }
}

private fun BuildType.requireDotNet() {
    requirements {
        exists(DotNet10Parameter)
    }
}

private fun cakeArguments(target: String, versionArguments: String) =
    "run --project build/Vivarium.Build.csproj -- --target $target " +
        "--source-sha %build.vcs.number% $versionArguments"

object Compile : BuildType({
    // Keep the stable ID so the existing history and counter remain attached to Compile.
    id("Vivarium_CompileWindowsX64")
    name = "Compile"
    buildNumberPattern = "%build.counter%"
    artifactRules = """
        out/build/** => build
        out/test-results/** => test-results
    """.trimIndent()
    commonVcs()
    steps {
        exec {
            name = "Build and test"
            path = "dotnet"
            arguments = cakeArguments("CI", "--build-counter %build.counter%")
        }
        exec {
            name = "Compile all platforms"
            path = "dotnet"
            arguments = cakeArguments("CompileAll", "--build-counter %build.counter%")
        }
        exec {
            name = "Native product smoke"
            path = "dotnet"
            arguments = cakeArguments("CompileSmoke", "--build-counter %build.counter%")
        }
    }
    features {
        xmlReport {
            reportType = XmlReport.XmlReportType.TRX
            rules = "+:out/**/*.trx"
            verbose = true
        }
    }
    triggers {
        vcs {
            branchFilter = "+:<default>"
        }
    }
    requireDotNet()
})

object Release : BuildType({
    id("Vivarium_Release")
    name = "Release"
    buildNumberPattern = "${Compile.depParamRefs.buildNumber}"
    maxRunningBuilds = 1
    artifactRules = "out/release/** => release"
    commonVcs()
    params {
        param(BuildVersionParameter, "${Compile.depParamRefs.buildNumber}")
    }
    steps {
        exec {
            name = "Package Compile artifacts"
            path = "dotnet"
            arguments = cakeArguments("Release", "--build-version %$BuildVersionParameter%")
        }
    }
    requireDotNet()
    dependencies {
        dependency(Compile) {
            snapshot {
                reuseBuilds = ReuseBuilds.NO
                onDependencyFailure = FailureAction.FAIL_TO_START
            }
            artifacts {
                cleanDestination = true
                artifactRules = "build/** => out/build"
            }
        }
    }
})

object PublishGitHub : BuildType({
    id("Vivarium_Publish")
    name = "Publish / GitHub"
    buildNumberPattern = "${Release.depParamRefs.buildNumber}"
    type = BuildTypeSettings.Type.DEPLOYMENT
    maxRunningBuilds = 1
    commonVcs()
    params {
        param(BuildVersionParameter, "${Release.depParamRefs.buildNumber}")
        param("env.GH_TOKEN", "%github.release.token%")
    }
    steps {
        exec {
            name = "Publish GitHub release"
            path = "dotnet"
            arguments = cakeArguments(
                "Publish",
                "--build-version %$BuildVersionParameter% --github-repository iXab3r/Vivarium")
        }
    }
    requireDotNet()
    dependencies {
        dependency(Release) {
            snapshot {
                reuseBuilds = ReuseBuilds.SUCCESSFUL
                onDependencyFailure = FailureAction.FAIL_TO_START
            }
            artifacts {
                cleanDestination = true
                artifactRules = "release/** => out/release"
            }
        }
    }
})

object PublishDocker : BuildType({
    id("Vivarium_PublishDocker")
    name = "Publish / Docker"
    buildNumberPattern = "${Release.depParamRefs.buildNumber}"
    type = BuildTypeSettings.Type.DEPLOYMENT
    maxRunningBuilds = 1
    commonVcs()
    params {
        param(BuildVersionParameter, "${Release.depParamRefs.buildNumber}")
        param(DockerImageParameter, DockerImage)
    }
    steps {
        exec {
            name = "Build linux-x64 server image"
            path = "docker"
            arguments = "build --pull --file build/docker/viv-server.Dockerfile " +
                "--build-arg VIVARIUM_VERSION=%$BuildVersionParameter% " +
                "--build-arg VIVARIUM_SOURCE_SHA=%build.vcs.number% " +
                "--tag %$DockerImageParameter%:%$BuildVersionParameter% out/docker/server"
        }
        exec {
            name = "Smoke server image"
            path = "docker"
            arguments = "run --rm %$DockerImageParameter%:%$BuildVersionParameter% --version"
        }
        exec {
            name = "Tag latest"
            path = "docker"
            arguments = "tag %$DockerImageParameter%:%$BuildVersionParameter% %$DockerImageParameter%:latest"
        }
        exec {
            name = "Push version"
            path = "docker"
            arguments = "push %$DockerImageParameter%:%$BuildVersionParameter%"
        }
        exec {
            name = "Push latest"
            path = "docker"
            arguments = "push %$DockerImageParameter%:latest"
        }
        exec {
            name = "Clean local image"
            executionMode = BuildStep.ExecutionMode.ALWAYS
            path = "/bin/sh"
            arguments = "-c \"docker image rm --force " +
                "%$DockerImageParameter%:%$BuildVersionParameter% %$DockerImageParameter%:latest || true\""
        }
    }
    requirements {
        equals("docker.server.osType", "linux")
    }
    dependencies {
        dependency(Release) {
            snapshot {
                reuseBuilds = ReuseBuilds.SUCCESSFUL
                onDependencyFailure = FailureAction.FAIL_TO_START
            }
            artifacts {
                cleanDestination = true
                artifactRules = "release/viv-server-linux-x64.zip!** => out/docker/server"
            }
        }
    }
})

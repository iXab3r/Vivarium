package Vivarium

import jetbrains.buildServer.configs.kotlin.*
import jetbrains.buildServer.configs.kotlin.buildFeatures.XmlReport
import jetbrains.buildServer.configs.kotlin.buildFeatures.xmlReport
import jetbrains.buildServer.configs.kotlin.buildSteps.exec
import jetbrains.buildServer.configs.kotlin.triggers.vcs

private const val DotNet10Parameter = "DotNetCoreSDK10.0_Path"
private const val BuildCounterParameter = "VivariumBuildCounter"
private const val BuildVersionParameter = "VivariumBuildVersion"

private fun BuildType.commonVcs() {
    vcs {
        root(VivariumVcs)
        checkoutMode = CheckoutMode.ON_SERVER
        cleanCheckout = true
    }
}

private fun BuildType.importTrx() {
    features {
        xmlReport {
            reportType = XmlReport.XmlReportType.TRX
            rules = "+:out/**/*.trx"
            verbose = true
        }
    }
}

private fun BuildType.requireOs(osName: String, architecture: String) {
    requirements {
        contains("teamcity.agent.jvm.os.name", osName)
        contains("teamcity.agent.jvm.os.arch", architecture)
        exists(DotNet10Parameter)
    }
}

private fun BuildType.requireDotNet() {
    requirements {
        exists(DotNet10Parameter)
    }
}

private fun cakeArguments(target: String, versionArguments: String, extra: String = "") =
    "run --project build/Vivarium.Build.csproj -- --target $target " +
        "--source-sha %build.vcs.number% $versionArguments$extra"

object BuildNumber : BuildType({
    id("Vivarium_BuildNumber")
    name = "Build Number"
    maxRunningBuilds = 1
    vcs {
        checkoutMode = CheckoutMode.MANUAL
    }
})

private fun compileBuild(
    buildId: String,
    displayName: String,
    rid: String,
    osName: String,
    architecture: String,
    runTests: Boolean = false,
    triggerOnDefault: Boolean = false,
) = BuildType({
    id(buildId)
    name = displayName
    buildNumberPattern = "${BuildNumber.depParamRefs.buildNumber}"
    params {
        param(BuildCounterParameter, "${BuildNumber.depParamRefs.buildNumber}")
    }
    artifactRules = if (runTests) {
        """
            out/build/$rid/** => $rid
            out/test-results/** => test-results
        """.trimIndent()
    } else {
        "out/build/$rid/** => $rid"
    }
    commonVcs()
    steps {
        if (runTests) {
            exec {
                name = "Build and test"
                path = "dotnet"
                arguments = cakeArguments("CI", "--build-counter %$BuildCounterParameter%")
            }
        }
        exec {
            name = "Compile $rid"
            path = "dotnet"
            arguments = cakeArguments("Compile", "--build-counter %$BuildCounterParameter%", " --rid $rid")
        }
        exec {
            name = "Native product smoke"
            path = "dotnet"
            arguments = cakeArguments("CompileSmoke", "--build-counter %$BuildCounterParameter%", " --rid $rid")
        }
    }
    if (runTests) importTrx()
    if (triggerOnDefault) {
        triggers {
            vcs {
                branchFilter = "+:<default>"
            }
        }
    }
    requireOs(osName, architecture)
    dependencies {
        snapshot(BuildNumber) {
            reuseBuilds = ReuseBuilds.NO
            onDependencyFailure = FailureAction.FAIL_TO_START
        }
    }
})

val CompileWindowsX64 = compileBuild(
    "Vivarium_CompileWindowsX64",
    "Compile / Windows x64",
    "win-x64",
    "Windows",
    "amd64",
    runTests = true,
    triggerOnDefault = true)

val CompileLinuxX64 = compileBuild(
    "Vivarium_CompileLinuxX64",
    "Compile / Linux x64",
    "linux-x64",
    "Linux",
    "amd64")

val CompileLinuxArm64 = compileBuild(
    "Vivarium_CompileLinuxArm64",
    "Compile / Linux arm64",
    "linux-arm64",
    "Linux",
    "aarch64")

val CompileMacosArm64 = compileBuild(
    "Vivarium_CompileMacosArm64",
    "Compile / macOS arm64",
    "osx-arm64",
    "Mac",
    "aarch64")

object Release : BuildType({
    id("Vivarium_Release")
    name = "Release"
    buildNumberPattern = "${CompileWindowsX64.depParamRefs.buildNumber}"
    maxRunningBuilds = 1
    artifactRules = "out/release/** => release"
    commonVcs()
    params {
        param(BuildVersionParameter, "${CompileWindowsX64.depParamRefs.buildNumber}")
    }
    steps {
        exec {
            name = "Package and native-smoke Compile artifacts"
            path = "dotnet"
            arguments = cakeArguments("Release", "--build-version %$BuildVersionParameter%")
        }
    }
    requireDotNet()
    dependencies {
        for ((compile, rid) in listOf(
            CompileWindowsX64 to "win-x64",
            CompileLinuxX64 to "linux-x64",
            CompileLinuxArm64 to "linux-arm64",
            CompileMacosArm64 to "osx-arm64",
        )) {
            dependency(compile) {
                snapshot {
                    reuseBuilds = ReuseBuilds.NO
                    onDependencyFailure = FailureAction.FAIL_TO_START
                }
                artifacts {
                    cleanDestination = true
                    artifactRules = "$rid/** => out/build/$rid"
                }
            }
        }
    }
})

object Publish : BuildType({
    id("Vivarium_Publish")
    name = "Publish"
    buildNumberPattern = "${Release.depParamRefs.buildNumber}"
    type = BuildTypeSettings.Type.DEPLOYMENT
    paused = true
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
                "--build-version %$BuildVersionParameter%",
                " --github-repository iXab3r/Vivarium")
        }
    }
    requireOs("Linux", "amd64")
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

package Vivarium

import jetbrains.buildServer.configs.kotlin.*
import jetbrains.buildServer.configs.kotlin.buildFeatures.XmlReport
import jetbrains.buildServer.configs.kotlin.buildFeatures.xmlReport
import jetbrains.buildServer.configs.kotlin.buildSteps.exec
import jetbrains.buildServer.configs.kotlin.triggers.vcs

private const val DotNet10Parameter = "DotNetCoreSDK10.0_Path"

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

private fun BuildType.requireOs(osName: String, architecture: String? = null) {
    requirements {
        contains("teamcity.agent.jvm.os.name", osName)
        exists(DotNet10Parameter)
        if (architecture != null) contains("teamcity.agent.jvm.os.arch", architecture)
    }
}

private fun cakeArguments(target: String, extra: String = "") =
    "run --project build/Vivarium.Build.csproj -- --target $target " +
        "--source-sha %build.vcs.number% --build-counter %build.counter%$extra"

object VerifyWindows : BuildType({
    id("Vivarium_VerifyWindows")
    name = "Verify / Windows x64"
    artifactRules = "out/test-results/** => test-results"
    commonVcs()
    steps {
        exec {
            name = "Cake CI"
            path = "dotnet"
            arguments = cakeArguments("CI")
        }
        exec {
            name = "Native payload smoke"
            path = "dotnet"
            arguments = cakeArguments("PayloadSmoke", " --rid win-x64")
        }
    }
    importTrx()
    requireOs("Windows", "amd64")
})

object VerifyLinux : BuildType({
    id("Vivarium_VerifyLinux")
    name = "Verify / Linux x64"
    artifactRules = """
        out/test-results/** => test-results
        out/payload-cross-macos/** => payload-cross-macos
    """.trimIndent()
    commonVcs()
    steps {
        exec {
            name = "Cake CI"
            path = "dotnet"
            arguments = cakeArguments("CI")
        }
        exec {
            name = "Native payload smoke"
            path = "dotnet"
            arguments = cakeArguments("PayloadSmoke", " --rid linux-x64")
        }
        exec {
            name = "Cross-publish macOS payload"
            path = "dotnet"
            arguments = cakeArguments("PayloadCrossMacPublish")
        }
        exec {
            name = "Pinned nextest archive smoke"
            path = "dotnet"
            arguments = cakeArguments("PayloadNextest")
        }
    }
    importTrx()
    requireOs("Linux", "amd64")
})

object VerifyMacos : BuildType({
    id("Vivarium_VerifyMacos")
    name = "Verify / macOS arm64"
    artifactRules = "out/test-results/** => test-results"
    commonVcs()
    steps {
        exec {
            name = "Cake CI"
            path = "dotnet"
            arguments = cakeArguments("CI")
        }
        exec {
            name = "Native payload smoke"
            path = "dotnet"
            arguments = cakeArguments("PayloadSmoke", " --rid osx-arm64")
        }
        exec {
            name = "Run Linux-produced macOS payload"
            path = "dotnet"
            arguments = cakeArguments(
                "PayloadCrossMacRun",
                " --payload-directory %teamcity.build.checkoutDir%/out/payload-cross-macos")
        }
    }
    importTrx()
    requireOs("Mac", "aarch64")
    dependencies {
        dependency(VerifyLinux) {
            snapshot {
                reuseBuilds = ReuseBuilds.NO
                onDependencyFailure = FailureAction.FAIL_TO_START
            }
            artifacts {
                cleanDestination = true
                artifactRules = "payload-cross-macos/** => out/payload-cross-macos"
            }
        }
    }
})

object CiGate : BuildType({
    id("Vivarium_CiGate")
    name = "CI gate"
    type = BuildTypeSettings.Type.COMPOSITE
    vcs {
        showDependenciesChanges = true
    }
    triggers {
        vcs {
            branchFilter = "+:<default>"
        }
    }
    dependencies {
        for (dependency in listOf(VerifyWindows, VerifyLinux, VerifyMacos)) {
            snapshot(dependency) {
                reuseBuilds = ReuseBuilds.NO
                onDependencyFailure = FailureAction.FAIL_TO_START
            }
        }
    }
})

object ReleasePackage : BuildType({
    id("Vivarium_ReleasePackage")
    name = "Release / Assemble candidate"
    type = BuildTypeSettings.Type.DEPLOYMENT
    maxRunningBuilds = 1
    artifactRules = "out/release/** => release"
    commonVcs()
    steps {
        exec {
            name = "Assemble and verify release"
            path = "dotnet"
            arguments = cakeArguments(
                "ReleasePackage",
                " --release-version %teamcity.build.branch%")
        }
    }
    requireOs("Linux", "amd64")
    dependencies {
        snapshot(CiGate) {
            reuseBuilds = ReuseBuilds.NO
            onDependencyFailure = FailureAction.FAIL_TO_START
        }
    }
})

private fun releaseSmoke(
    buildId: String,
    displayName: String,
    rid: String,
    osName: String,
    architecture: String,
) = BuildType({
    id(buildId)
    name = displayName
    commonVcs()
    steps {
        exec {
            name = "Verify exact candidate ZIPs"
            path = "dotnet"
            arguments = cakeArguments(
                "ReleaseSmoke",
                " --rid $rid --release-version %teamcity.build.branch%")
        }
    }
    requireOs(osName, architecture)
    dependencies {
        dependency(ReleasePackage) {
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

val ReleaseSmokeWindows = releaseSmoke(
    "Vivarium_ReleaseSmokeWindows",
    "Release smoke / Windows x64",
    "win-x64",
    "Windows",
    "amd64")

val ReleaseSmokeLinux = releaseSmoke(
    "Vivarium_ReleaseSmokeLinux",
    "Release smoke / Linux x64",
    "linux-x64",
    "Linux",
    "amd64")

val ReleaseSmokeLinuxArm64 = releaseSmoke(
    "Vivarium_ReleaseSmokeLinuxArm64",
    "Release smoke / Linux arm64",
    "linux-arm64",
    "Linux",
    "aarch64")

val ReleaseSmokeMacos = releaseSmoke(
    "Vivarium_ReleaseSmokeMacos",
    "Release smoke / macOS arm64",
    "osx-arm64",
    "Mac",
    "aarch64")

object ReleaseGate : BuildType({
    id("Vivarium_ReleaseGate")
    name = "Release gate"
    type = BuildTypeSettings.Type.COMPOSITE
    vcs {
        showDependenciesChanges = true
    }
    dependencies {
        for (dependency in listOf(
            ReleaseSmokeWindows,
            ReleaseSmokeLinux,
            ReleaseSmokeLinuxArm64,
            ReleaseSmokeMacos,
        )) {
            snapshot(dependency) {
                reuseBuilds = ReuseBuilds.NO
                onDependencyFailure = FailureAction.FAIL_TO_START
            }
        }
    }
})

object PublishGitHub : BuildType({
    id("Vivarium_PublishGitHub")
    name = "Release / Publish GitHub"
    type = BuildTypeSettings.Type.DEPLOYMENT
    paused = true
    maxRunningBuilds = 1
    commonVcs()
    params {
        param("env.GH_TOKEN", "%github.release.token%")
    }
    steps {
        exec {
            name = "Draft, verify, and publish immutable release"
            path = "dotnet"
            arguments = cakeArguments(
                "ReleasePublish",
                " --release-version %teamcity.build.branch% --github-repository iXab3r/Vivarium")
        }
    }
    requireOs("Linux", "amd64")
    dependencies {
        snapshot(ReleaseGate) {
            reuseBuilds = ReuseBuilds.NO
            onDependencyFailure = FailureAction.FAIL_TO_START
        }
        dependency(ReleasePackage) {
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

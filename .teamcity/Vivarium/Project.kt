package Vivarium

import jetbrains.buildServer.configs.kotlin.*
import jetbrains.buildServer.configs.kotlin.Project

object Project : Project({
    description = "Cross-platform Vivarium compile, release, and publish pipeline"

    vcsRoot(VivariumVcs)

    buildType(Compile)
    buildType(Release)
    buildType(Publish)

    buildTypesOrder = arrayListOf(
        Compile,
        Release,
        Publish,
    )
})

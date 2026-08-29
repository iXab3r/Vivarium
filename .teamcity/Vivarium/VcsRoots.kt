package Vivarium

import jetbrains.buildServer.configs.kotlin.*
import jetbrains.buildServer.configs.kotlin.vcs.GitVcsRoot

object VivariumVcs : GitVcsRoot({
    id("Vivarium_Vcs")
    name = "Vivarium"
    url = "https://github.com/iXab3r/Vivarium.git"
    branch = "refs/heads/main"
    branchSpec = """
        +:refs/heads/(*)
    """.trimIndent()
})

import type { IDisposable } from "@eyeauras/workbench";
import { WorkbenchButton } from "@eyeauras/workbench/controls";
import { api, ApiError, session, type Page } from "./api";

interface Project { id: string; name: string }
interface Configuration { id: string; name: string; projectId: string; activeRevisionId: string;
    snapshotDependencies?: string[]; artifactDependencies?: string[]; requirements?: { name: string; value: string }[] }
interface Selection { id: number; configurationId: string; state: string; status: string }
interface Build { id: number; configurationId: string; projectId: string; number: string | null;
    state: string; status: string; statusText: string; agentId: string | null;
    snapshotDependencies?: Selection[]; artifactDependencies?: Selection[] }
interface Artifact { path: string; length: number; sha256: string; href: string }
interface Output { items: { kind: string; reason?: string; content?: { href: string } }[];
    page: { nextCursor: string | null }; output: { state: string; resumeCursor: string } }

export function renderBuilds(host: HTMLElement): IDisposable {
    host.classList.add("builds-page");
    host.innerHTML = '<header class="toolbar"><h1>Builds</h1></header><p class="notice" role="status"></p><div class="builds-body"><nav class="project-list" aria-label="Projects and configurations"></nav><section class="build-inspector" aria-label="Selected configuration or build"></section></div>';
    const cancel = new AbortController(), listControls: IDisposable[] = [], detailControls: IDisposable[] = [];
    const list = host.querySelector<HTMLElement>(".project-list")!, detail = host.querySelector<HTMLElement>(".build-inspector")!;
    const notice = host.querySelector<HTMLElement>(".notice")!;
    let generation = 0, listGeneration = 0, timer: ReturnType<typeof setTimeout> | undefined, mutating = false;
    const dispose = (items: IDisposable[]) => items.splice(0).forEach(item => item.dispose());
    function element<K extends keyof HTMLElementTagNameMap>(parent: HTMLElement, tag: K, text: string) {
        const node = document.createElement(tag); node.textContent = text; parent.append(node); return node;
    }
    function button(parent: HTMLElement, label: string, action: () => void, owned = detailControls) {
        const control = new WorkbenchButton(parent, { label, secondary: true });
        owned.push(control, control.onDidClick(action)); return control;
    }
    function fail(error: unknown) {
        if (cancel.signal.aborted) return;
        notice.textContent = error instanceof ApiError ? error.message : "Service unavailable. Refresh to try again.";
        notice.setAttribute("role", "alert");
    }
    function navigate(path: string) { history.pushState(null, "", path); }
    function resetDetail() { clearTimeout(timer); dispose(detailControls); detail.replaceChildren(); notice.textContent = ""; }
    function configurationLinks(label: string, ids: string[]) {
        if (!ids.length) return;
        const section = element(detail, "section", ""); section.setAttribute("aria-label", label);
        element(section, "h3", label);
        for (const id of ids) {
            const link = element(section, "a", id); link.href = "/build-configurations/" + encodeURIComponent(id);
            link.className = "chain-link";
        }
    }
    function buildLinks(label: string, selections: Selection[]) {
        const states = new Map<number, HTMLElement>();
        if (!selections.length) return states;
        const section = element(detail, "section", ""); section.setAttribute("aria-label", label);
        element(section, "h3", label);
        for (const selected of selections) {
            const row = element(section, "div", ""); row.className = "chain-row";
            const link = element(row, "a", selected.configurationId); link.href = "/builds/" + selected.id;
            states.set(selected.id, element(row, "span", `Build ${selected.id} · ${selected.state} · ${selected.status}`));
        }
        return states;
    }
    async function projects(offset = 0) {
        const request = ++listGeneration;
        try {
            const page = await api<Page<Project>>(`projects?offset=${offset}&limit=50`, cancel.signal);
            if (cancel.signal.aborted || request !== listGeneration) return;
            dispose(listControls); list.replaceChildren(); element(list, "h2", "Projects");
            if (!page.items.length) element(list, "p", "No accessible projects.");
            for (const project of page.items) button(list, project.id, () => {
                navigate("/projects/" + encodeURIComponent(project.id)); void projectConfigurations(project.id);
            }, listControls);
            const paging = element(list, "div", ""); paging.className = "actions";
            button(paging, "Previous projects", () => { void projects(Math.max(0, offset - 50)); }, listControls).enabled = offset > 0;
            button(paging, "Next projects", () => { void projects(offset + 50); }, listControls).enabled = offset + page.items.length < page.total;
        } catch (error) { fail(error); }
    }
    async function projectConfigurations(project: string, offset = 0) {
        const request = ++generation; resetDetail();
        try {
            const page = await api<Page<Configuration>>(`projects/${encodeURIComponent(project)}/build-configurations?offset=${offset}&limit=50`, cancel.signal);
            if (cancel.signal.aborted || request !== generation) return;
            element(detail, "h2", project);
            if (!page.items.length) element(detail, "p", "No active configurations.");
            for (const configuration of page.items) button(detail, configuration.name, () => {
                navigate("/build-configurations/" + encodeURIComponent(configuration.id)); void showConfiguration(configuration.id);
            });
            const paging = element(detail, "div", ""); paging.className = "actions";
            button(paging, "Previous configurations", () => { void projectConfigurations(project, Math.max(0, offset - 50)); }).enabled = offset > 0;
            button(paging, "Next configurations", () => { void projectConfigurations(project, offset + 50); }).enabled = offset + page.items.length < page.total;
        } catch (error) { fail(error); }
    }
    async function showConfiguration(id: string) {
        const request = ++generation; resetDetail();
        try {
            const configuration = await api<Configuration>("build-configurations/" + encodeURIComponent(id), cancel.signal);
            if (cancel.signal.aborted || request !== generation) return;
            element(detail, "h2", configuration.name);
            element(detail, "p", "Active definition: " + configuration.activeRevisionId);
            element(detail, "h3", "Agent requirements");
            for (const requirement of configuration.requirements ?? []) element(detail, "p", requirement.name + " = " + requirement.value);
            if (!configuration.requirements?.length) element(detail, "p", "No configured requirements.");
            configurationLinks("Snapshot dependencies", configuration.snapshotDependencies ?? []);
            configurationLinks("Artifact producers", configuration.artifactDependencies ?? []);
            const run = button(detail, "Run " + configuration.name, () => {
                if (mutating) return; mutating = true; run.enabled = false; notice.textContent = "Submitting…";
                void api<Build>("builds", cancel.signal, "POST", { configurationId: id }).then(build => {
                    navigate("/builds/" + build.id); return showBuild(build.id);
                }).catch(fail).finally(() => { mutating = false; run.enabled = true; });
            });
        } catch (error) { fail(error); }
    }
    async function artifacts(id: number, container: HTMLElement, request: number, cursor?: string) {
        try {
            const page = await api<{ items: Artifact[]; page: { nextCursor: string | null } }>(`builds/${id}/artifacts?limit=50`
                + (cursor ? "&cursor=" + encodeURIComponent(cursor) : ""), cancel.signal);
            if (cancel.signal.aborted || request !== generation) return;
            container.replaceChildren();
            if (!page.items.length) element(container, "p", "No artifacts.");
            for (const file of page.items) {
                const row = element(container, "div", ""); row.className = "artifact-row";
                const link = element(row, "a", file.path); link.href = file.href; link.download = file.path.split("/").pop()!;
                element(row, "span", `${file.length} bytes · SHA256 ${file.sha256}`);
            }
            if (page.page.nextCursor) button(container, "More artifacts", () => { void artifacts(id, container, request, page.page.nextCursor!); });
        } catch (error) { fail(error); }
    }
    async function showBuild(id: number) {
        const request = ++generation; clearTimeout(timer);
        try {
            const build = await api<Build>("builds/" + id, cancel.signal);
            if (cancel.signal.aborted || request !== generation) return;
            resetDetail(); element(detail, "h2", `${build.configurationId} #${build.number ?? build.id}`);
            const status = element(detail, "p", `${build.state} · ${build.status} · ${build.statusText}`); status.dataset.testid = "build-status";
            const agent = element(detail, "p", build.agentId ? "Agent: " + build.agentId : "No Agent assigned.");
            const actions = element(detail, "div", ""); actions.className = "actions";
            button(actions, "Refresh build", () => { void showBuild(id); });
            let stop: WorkbenchButton | undefined;
            if (build.state !== "finished") {
                stop = button(actions, "Cancel build", () => {
                    if (mutating) return; mutating = true; stop!.enabled = false;
                    void api(`builds/${id}/cancellation`, cancel.signal, "PUT", { reason: "Canceled through Workbench", mode: "graceful" })
                        .then(() => showBuild(id)).catch(fail).finally(() => { mutating = false; });
                }); stop.enabled = !mutating;
            }
            const snapshotStates = buildLinks("Snapshot dependencies", build.snapshotDependencies ?? []);
            const artifactStates = buildLinks("Selected artifact producers", build.artifactDependencies ?? []);
            element(detail, "h3", "Output");
            const log = element(detail, "pre", ""); log.className = "build-log"; log.dataset.testid = "build-log";
            let cursor: string | undefined;
            async function readLogs(reset: boolean) {
                log.textContent = "Loading output…";
                try {
                    const page = await api<Output>(`builds/${id}/output?limit=1` + (!reset && cursor ? "&cursor=" + encodeURIComponent(cursor) : ""), cancel.signal);
                    let text = "";
                    for (const entry of page.items) {
                        if (text.length >= 65536) break;
                        if (entry.kind === "gap") text += `[Output unavailable: ${entry.reason}]\n`;
                        else if (entry.content) {
                            const response = await fetch(entry.content.href, { signal: cancel.signal, credentials: "same-origin", cache: "no-store", redirect: "error" });
                            if (!response.ok) throw new ApiError(response.status);
                            text += (await response.text()).slice(0, 65536 - text.length);
                        }
                    }
                    if (cancel.signal.aborted || request !== generation) return;
                    log.textContent = text || "No output on this page.";
                    cursor = page.output.resumeCursor; more.enabled = page.page.nextCursor !== null || page.output.state === "open";
                } catch (error) { log.textContent = "Output unavailable."; fail(error); }
            }
            const logActions = element(detail, "div", ""); logActions.className = "actions";
            button(logActions, "Read logs", () => { void readLogs(true); });
            const more = button(logActions, "More logs", () => { void readLogs(false); }); more.enabled = false;
            element(detail, "p", "Output is paged; at most 64 KiB is shown per page.");
            element(detail, "h3", "Artifacts"); const files = element(detail, "div", "Loading artifacts…");
            void artifacts(id, files, request);
            async function poll() {
                if (cancel.signal.aborted || request !== generation || location.pathname !== "/builds/" + id) return;
                if (mutating) { timer = setTimeout(poll, 1000); return; }
                try {
                    const current = await api<Build>("builds/" + id, cancel.signal);
                    if (cancel.signal.aborted || request !== generation) return;
                    notice.textContent = "";
                    status.textContent = `${current.state} · ${current.status} · ${current.statusText}`;
                    agent.textContent = current.agentId ? "Agent: " + current.agentId : "No Agent assigned.";
                    for (const [selections, states] of [[current.snapshotDependencies ?? [], snapshotStates], [current.artifactDependencies ?? [], artifactStates]] as const)
                        for (const selected of selections) {
                            const state = states.get(selected.id); if (state) state.textContent = `Build ${selected.id} · ${selected.state} · ${selected.status}`;
                        }
                    if (current.state === "finished") { if (stop) stop.enabled = false; void artifacts(id, files, request); return; }
                } catch (error) { fail(error); }
                timer = setTimeout(poll, 1000);
            }
            if (build.state !== "finished") timer = setTimeout(poll, 1000);
        } catch (error) { fail(error); }
    }
    function route() {
        const build = location.pathname.match(/^\/builds\/(\d+)$/)?.[1];
        const configuration = location.pathname.match(/^\/build-configurations\/([^/]+)$/)?.[1];
        const project = location.pathname.match(/^\/projects\/([^/]+)$/)?.[1];
        if (build) void showBuild(Number(build));
        else if (configuration) void showConfiguration(decodeURIComponent(configuration));
        else if (project) void projectConfigurations(decodeURIComponent(project));
        else { ++generation; resetDetail(); detail.textContent = "Select a project and configuration."; }
    }
    if (!session.user) notice.textContent = "Sign in to view builds.";
    else { void projects(); route(); }
    window.addEventListener("popstate", route);
    return { dispose() { cancel.abort(); ++generation; ++listGeneration; clearTimeout(timer);
        window.removeEventListener("popstate", route); dispose(listControls); dispose(detailControls); host.classList.remove("builds-page"); } };
}

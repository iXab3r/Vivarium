import { createWorkbench, type IDisposable } from "@eyeauras/workbench";
import { WorkbenchButton } from "@eyeauras/workbench/controls";
import "@vscode/codicons/dist/codicon.css";
import "./style.css";
import { api, ApiError, session, type User, type Role, type Grant, type Page } from "./api";
import { renderBuilds } from "./builds";

const app = document.querySelector<HTMLElement>("#app")!;
app.innerHTML = '<header class="app-header"><a class="brand" href="/">Vivarium</a><div class="account"></div></header><div id="workspace"></div>';
if (session.localDevelopment) {
    const guide = document.createElement("aside");
    guide.className = "notice";
    guide.innerHTML = '<strong>Local development examples</strong> · <a href="/projects/Local">Open the sample project</a>. Run Success for a real log and proof.txt artifact, Failure for exit 7, or Cancel me to practice stopping a Build. Inspect build history in each configuration; step declarations are in the local Git source. Edit sources under artifacts/local-dev/lab/definitions and commit, then reconcile. Host restart rotates the password in operator.private.json.';
    app.querySelector("header")!.after(guide);
}
const account = app.querySelector<HTMLElement>(".account")!;
if (session.user) {
    const name = document.createElement("span");
    name.textContent = session.user.displayName + (session.user.isInstallation ? " · Installation account" : "");
    account.append(name);
    if (!session.user.isInstallation) {
        const keys = document.createElement("a"); keys.href = "/Identity/Account/Manage/Tokens"; keys.textContent = "My keys";
        account.append(keys);
    }
    const form = document.createElement("form"); form.method = "post"; form.action = "/Identity/Account/Logout";
    const token = document.createElement("input"); token.type = "hidden"; token.name = "__RequestVerificationToken";
    token.value = session.requestVerificationToken;
    const logout = document.createElement("button"); logout.type = "submit"; logout.textContent = "Sign out";
    form.append(token, logout); account.append(form);
} else {
    const signIn = document.createElement("a"); signIn.href = "/Identity/Account/Login?ReturnUrl=%2Faccess";
    signIn.textContent = "Sign in"; account.append(signIn);
}
const workbench = createWorkbench(app.querySelector<HTMLElement>("#workspace")!, {
    productName: "Vivarium", title: "Vivarium", showTitlebar: false, showPageTabs: false,
    showEditorSplitActions: false, railDensity: "regular",
});
workbench.registerPage({ id: "access", title: "Access", render: renderAccess });
workbench.registerPage({ id: "builds", title: "Builds", render: renderBuilds });
const isBuilds = () => (session.localDevelopment && location.pathname === "/") || /^\/(builds|projects|build-configurations)(\/|$)/.test(location.pathname);
workbench.registerToggle({ id: "builds", title: "Builds", icon: "tools", rail: "sidebar", isActive: isBuilds,
    run() { location.assign("/builds"); } });
workbench.registerToggle({ id: "access", title: "Access", icon: "shield", rail: "sidebar", isActive: () => !isBuilds(),
    run() { location.assign("/access"); } });
workbench.showPage(isBuilds() ? "builds" : "access");
window.addEventListener("pagehide", event => { if (!event.persisted) workbench.dispose(); });

function renderAccess(host: HTMLElement): IDisposable {
    host.classList.add("access-page");
    host.innerHTML = '<header class="toolbar"><h1>Access</h1><div class="actions"></div></header><p class="notice" role="status"></p><div class="access-body"><section class="users" aria-label="Users"></section><section class="inspector" aria-label="User access"></section></div>';
    const cancellation = new AbortController();
    const controls: IDisposable[] = [];
    const listControls: IDisposable[] = [], detailControls: IDisposable[] = [];
    const dispose = (items: IDisposable[]) => items.splice(0).forEach(item => item.dispose());
    const usersHost = host.querySelector<HTMLElement>(".users")!;
    const detail = host.querySelector<HTMLElement>(".inspector")!;
    const notice = host.querySelector<HTMLElement>(".notice")!;
    let offset = 0, roleOffset = 0, generation = 0, listGeneration = 0;
    const initialId = location.pathname.match(/^\/access\/users\/([a-f0-9-]+)$/i)?.[1];
    let selected = initialId ?? "";
    function fail(error: unknown) {
        if (cancellation.signal.aborted) return;
        notice.textContent = error instanceof ApiError ? error.message : "Service unavailable.";
        notice.setAttribute("role", "alert");
    }
    function button(container: HTMLElement, label: string, action: () => void, owned = controls) {
        const control = new WorkbenchButton(container, { label, secondary: true });
        owned.push(control, control.onDidClick(action)); return control;
    }
    if (!session.user || !session.canReadProfiles) {
        notice.textContent = session.user ? "Current permissions do not allow access management." : "Sign in to manage access.";
        return { dispose() { cancellation.abort(); host.classList.remove("access-page"); } };
    }
    button(host.querySelector(".actions")!, "Refresh", () => { void load(); });
    async function load() {
        const request = ++listGeneration;
        notice.textContent = "Loading…";
        try {
            const page = await api<Page<User>>(`users?offset=${offset}&limit=50`, cancellation.signal);
            if (cancellation.signal.aborted || request !== listGeneration) return;
            dispose(listControls); usersHost.replaceChildren();
            const heading = document.createElement("h2"); heading.textContent = `Users (${page.total})`; usersHost.append(heading);
            for (const user of page.items) button(usersHost, user.displayName || user.userName,
                () => { selected = user.id; roleOffset = 0; history.pushState(null, "", "/access/users/" + user.id); void inspect(user); }, listControls);
            const paging = document.createElement("div"); paging.className = "actions"; usersHost.append(paging);
            const previous = button(paging, "Previous users", () => { offset = Math.max(0, offset - 50); void load(); }, listControls);
            previous.enabled = offset > 0;
            const next = button(paging, "Next users", () => { offset += 50; void load(); }, listControls);
            next.enabled = offset + page.items.length < page.total;
            notice.textContent = page.total === 0 ? "No users." : "";
            const user = page.items.find(user => user.id === selected) ?? (!initialId ? page.items[0] : undefined);
            if (user) await inspect(user);
            else if (selected) await inspect(await api<User>(`users/${selected}`, cancellation.signal));
            else detail.textContent = "Select a user.";
        } catch (error) { fail(error); }
    }
    async function inspect(user: User) {
        const current = ++generation;
        detail.textContent = "Loading access…";
        try {
            const roles = await api<Page<Role>>(`roles?offset=${roleOffset}&limit=50`, cancellation.signal);
            const query = new URLSearchParams({ limit: "200", scope: "g" });
            roles.items.forEach(role => query.append("roleId", role.id));
            const grants = await api<Page<Grant>>(`users/${user.id}/roles?${query}`, cancellation.signal);
            if (cancellation.signal.aborted || current !== generation) return;
            dispose(detailControls); detail.replaceChildren();
            const heading = document.createElement("h2"); heading.textContent = user.displayName || user.userName;
            const state = document.createElement("p"); state.textContent = user.isInstallation ? "Installation account" : user.isEnabled ? "Enabled" : "Disabled";
            detail.append(heading, state);
            const title = document.createElement("h3"); title.textContent = "Roles for all projects"; detail.append(title);
            for (const role of roles.items) {
                const label = document.createElement("label"); label.className = "role-assignment";
                const check = document.createElement("input"); check.type = "checkbox";
                check.checked = grants.items.some(grant => grant.roleId === role.id && grant.scope === "g");
                check.disabled = !session.canGrantRoles || user.isInstallation;
                label.append(check, document.createTextNode(role.name)); detail.append(label);
                check.onchange = async () => {
                    check.disabled = true; notice.textContent = "Saving…";
                    try {
                        await api(`users/${user.id}/roles/${encodeURIComponent(role.id)}/g`, cancellation.signal,
                            check.checked ? "PUT" : "DELETE");
                        notice.textContent = "Saved.";
                    } catch (error) { check.checked = !check.checked; fail(error); }
                    finally { if (current === generation) check.disabled = !session.canGrantRoles || user.isInstallation; }
                };
            }
            const paging = document.createElement("div"); paging.className = "actions"; detail.append(paging);
            const previous = button(paging, "Previous roles", () => { roleOffset = Math.max(0, roleOffset - 50); void inspect(user); }, detailControls);
            previous.enabled = roleOffset > 0;
            const next = button(paging, "Next roles", () => { roleOffset += 50; void inspect(user); }, detailControls);
            next.enabled = roleOffset + roles.items.length < roles.total;
            if (!user.isInstallation && session.canChangeUsers) button(detail, user.isEnabled ? "Disable account" : "Enable account", () => {
                void api(`users/${user.id}/enabled`, cancellation.signal, "PUT", { enabled: !user.isEnabled })
                    .then(() => { notice.textContent = "Saved."; return load(); }).catch(fail);
            }, detailControls);
        } catch (error) { fail(error); }
    }
    const pop = () => { selected = location.pathname.match(/^\/access\/users\/([a-f0-9-]+)$/i)?.[1] ?? ""; void load(); };
    window.addEventListener("popstate", pop);
    void load();
    return { dispose() { cancellation.abort(); generation++; window.removeEventListener("popstate", pop);
        dispose(controls); dispose(listControls); dispose(detailControls); host.classList.remove("access-page"); } };
}

/*
 * Shellvis bridge for Thunderbird.
 *
 * Thunderbird has no COM interface, so this extension is the only supported way in. It
 * connects to a native messaging host, which relays to Shellvis over a named pipe:
 *
 *     Thunderbird  <--stdio-->  Shellvis.Thunderbird.Host.exe  <--pipe-->  Shellvis
 *
 * Two rules shape everything below.
 *
 * It NEVER SENDS. Replies and new messages are saved as drafts through
 * compose.beginReply / beginNew with the compose window suppressed. A wrong draft is an
 * inconvenience; a wrongly sent mail cannot be recalled, and an agent working from a
 * summarised reading of a thread will sometimes be wrong.
 *
 * Every reply stays under Thunderbird's 1 MB native-message ceiling. Exceeding it
 * disconnects the port rather than truncating, so bodies are cut here, where the length is
 * known, rather than hoped about downstream.
 */

const MAX_BODY = 200 * 1024;
const MAX_LIST = 200;

let port = null;

/** Connect, and keep reconnecting: the host exits if Thunderbird restarts it. */
function connect() {
  port = browser.runtime.connectNative("media.ippen.shellvis");

  port.onMessage.addListener(async (request) => {
    let response;

    try {
      response = {
        ok: true,
        payload: JSON.stringify(await handle(request)),
        request_id: request.request_id,
      };
    } catch (error) {
      // Reported rather than thrown away: the host is waiting, and a dropped reply
      // becomes a 30 second timeout that says nothing about what went wrong.
      response = {
        ok: false,
        error: String(error && error.message ? error.message : error),
        request_id: request.request_id,
      };
    }

    port.postMessage(response);
  });

  port.onDisconnect.addListener(() => {
    port = null;
    // Thunderbird respawns the host on the next connect, so a delay avoids a tight
    // loop while Shellvis is not installed.
    setTimeout(connect, 5000);
  });
}

async function handle(request) {
  const args = request.arguments || {};

  switch (request.operation) {
    case "ping":
      return { thunderbird: (await browser.runtime.getBrowserInfo()).version };

    case "list_folders":
      return await listFolders();

    case "list_messages":
      return await listMessages(args);

    case "read_message":
      return await readMessage(args.id);

    case "draft_reply":
      return await draftReply(args);

    case "draft_message":
      return await draftMessage(args);

    default:
      throw new Error(`unknown operation '${request.operation}'`);
  }
}

async function listFolders() {
  const accounts = await browser.accounts.list();
  const folders = [];

  for (const account of accounts) {
    for (const folder of flatten(account.folders || [])) {
      let info = { total: 0, unread: 0 };

      try {
        info = await browser.folders.getFolderInfo(folder);
      } catch (e) {
        // Some virtual folders have no info. Listing them with zeroes is better than
        // omitting them, because the agent may still want to look inside.
      }

      folders.push({
        id: folder.path,
        name: `${account.name}${folder.path}`,
        total: info.totalMessageCount || 0,
        unread: info.unreadMessageCount || 0,
      });
    }
  }

  return folders;
}

/** Subfolders are nested; the agent addresses folders by path, so the tree is flattened. */
function flatten(folders) {
  const out = [];

  for (const folder of folders) {
    out.push(folder);

    if (folder.subFolders && folder.subFolders.length) {
      out.push(...flatten(folder.subFolders));
    }
  }

  return out;
}

async function listMessages(args) {
  const limit = Math.min(parseInt(args.limit || "20", 10) || 20, MAX_LIST);

  const query = { };

  if (args.unreadOnly === "true") {
    query.unread = true;
  }

  if (args.folder) {
    const folder = await findFolder(args.folder);

    if (!folder) {
      throw new Error(`no folder with path '${args.folder}'`);
    }

    query.folderId = folder.id !== undefined ? folder.id : undefined;
    query.folder = folder;
  }

  const page = await browser.messages.query(query);
  const messages = (page.messages || []).slice(0, limit);

  return messages.map((m) => ({
    id: String(m.id),
    subject: m.subject || "",
    author: m.author || "",
    date: m.date ? new Date(m.date).toISOString() : null,
    unread: !!m.new || m.read === false,
  }));
}

async function findFolder(path) {
  for (const account of await browser.accounts.list()) {
    for (const folder of flatten(account.folders || [])) {
      if (folder.path === path) {
        return folder;
      }
    }
  }

  return null;
}

async function readMessage(id) {
  const numeric = parseInt(id, 10);

  if (isNaN(numeric)) {
    throw new Error(`'${id}' is not a message id; use one from list_messages`);
  }

  const header = await browser.messages.get(numeric);
  const full = await browser.messages.getFull(numeric);

  let body = collectText(full);

  if (body.length > MAX_BODY) {
    // Truncated here, where the length is known. Thunderbird disconnects the native
    // port on an oversized message instead of truncating it, and that failure is a
    // silent hang.
    body = body.slice(0, MAX_BODY) + `\n... truncated at ${MAX_BODY} characters.`;
  }

  return [
    `From: ${header.author}`,
    `To: ${(header.recipients || []).join(", ")}`,
    `Date: ${header.date}`,
    `Subject: ${header.subject}`,
    "",
    body,
  ].join("\n");
}

/** Prefer text/plain; fall back to stripped HTML rather than returning markup. */
function collectText(part) {
  if (!part) {
    return "";
  }

  if (part.body && part.contentType && part.contentType.startsWith("text/plain")) {
    return part.body;
  }

  let text = "";

  for (const child of part.parts || []) {
    text += collectText(child);
  }

  if (!text && part.body) {
    text = part.body.replace(/<[^>]+>/g, " ").replace(/\s+/g, " ");
  }

  return text;
}

async function draftReply(args) {
  const numeric = parseInt(args.id, 10);

  if (isNaN(numeric)) {
    throw new Error(`'${args.id}' is not a message id`);
  }

  const tab = await browser.compose.beginReply(
    numeric,
    args.replyAll === "true" ? "replyToAll" : "replyToSender",
    { body: escapeHtml(args.body || ""), isPlainText: false }
  );

  await browser.compose.saveMessage(tab.id, { mode: "draft" });
  await browser.tabs.remove(tab.id);

  return { saved: "draft" };
}

async function draftMessage(args) {
  const tab = await browser.compose.beginNew({
    to: (args.to || "").split(/[;,]/).map((s) => s.trim()).filter(Boolean),
    subject: args.subject || "",
    body: escapeHtml(args.body || ""),
    isPlainText: false,
  });

  await browser.compose.saveMessage(tab.id, { mode: "draft" });
  await browser.tabs.remove(tab.id);

  return { saved: "draft" };
}

/*
 * The body is composed by a language model from text it read somewhere, so it is escaped
 * before it becomes HTML. Without this, a mail containing markup could put markup into the
 * draft -- an injection into the user's own outgoing message.
 */
function escapeHtml(text) {
  return String(text)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/\n/g, "<br>");
}

connect();

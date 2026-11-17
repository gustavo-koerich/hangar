"use strict";

// ---- ponte com o C# (WebView2) ----
const pendentes = new Map();
let seq = 0;
function chamar(acao, projeto, valor) {
  return new Promise((ok, falha) => {
    const id = ++seq;
    pendentes.set(id, { ok, falha });
    window.chrome.webview.postMessage({ id, acao, projeto, valor });
  });
}
window.chrome.webview.addEventListener("message", (e) => {
  const r = e.data, p = pendentes.get(r.id);
  if (!p) return;
  pendentes.delete(r.id);
  r.ok ? p.ok(r.resultado) : p.falha(new Error(r.erro));
});

// ---- estado ----
const SITUACOES = {
  "no-ar": { rotulo: "No ar", grupo: "No ar (mini servidor)" },
  desenvolvimento: { rotulo: "Em desenvolvimento", grupo: "Em desenvolvimento (mexido nos últimos 30 dias)" },
  parado: { rotulo: "Parado", grupo: "Parados (mais de 30 dias sem mexer)" },
  arquivado: { rotulo: "Arquivado", grupo: "Arquivados" },
};
const filtro = { area: "", situacao: "", busca: "" };
let panorama = null;
let aberto = null;
const ligando = new Map(); // id → "Ligando…" | "Desligando…"

const $ = (s) => document.querySelector(s);
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const nome = (p) => p.ficha?.nome || p.nomePasta;

function quando(iso) {
  if (!iso) return "sem data";
  // Dias de calendário (ontem à noite é "ontem", não "hoje").
  const meiaNoite = (x) => new Date(x.getFullYear(), x.getMonth(), x.getDate());
  const dias = Math.round((meiaNoite(new Date()) - meiaNoite(new Date(iso))) / 86400000);
  if (dias <= 0) return "hoje";
  if (dias === 1) return "ontem";
  if (dias < 30) return `há ${dias} dias`;
  if (dias < 365) return `há ${Math.round(dias / 30)} ${Math.round(dias / 30) === 1 ? "mês" : "meses"}`;
  return `há ${Math.floor(dias / 365)} ano(s)`;
}
const data = (iso) => (iso ? new Date(iso).toLocaleDateString("pt-BR") : "—");
const dataHora = (iso) => (iso ? new Date(iso).toLocaleString("pt-BR", { dateStyle: "short", timeStyle: "short" }) : "—");

function aviso(texto, ms = 2200) {
  const a = $("#aviso");
  a.textContent = texto;
  a.hidden = false;
  clearTimeout(aviso.t);
  aviso.t = setTimeout(() => (a.hidden = true), ms);
}

// ---- carregar ----
async function carregar() {
  $("#atualizar").disabled = true;
  $("#resumo-topo").textContent = "Lendo projetos, git e Docker…";
  try {
    panorama = await chamar("listar");
    desenhar();
    if (aberto) {
      const p = panorama.projetos.find((x) => x.id === aberto);
      p ? abrirDetalhe(p.id) : fecharDetalhe();
    }
  } catch (e) {
    $("#lista").innerHTML = `<p class="vazio">Não consegui ler os projetos: ${esc(e.message)}</p>`;
  } finally {
    $("#atualizar").disabled = false;
  }
}

// ---- desenhar ----
function seloGit(p) {
  const g = p.git;
  if (!g) return `<span class="selo" title="Sem controle de versão: só existe nesta pasta">sem git</span>`;
  if (!g.remoto) return `<span class="selo atencao" title="Tem git, mas não está no GitHub">sem GitHub</span>`;
  if (g.pendentes || g.naoEnviados)
    return `<span class="selo atencao" title="${g.pendentes} não salvas · ${g.naoEnviados} não enviadas">${g.pendentes + g.naoEnviados} pendente(s)</span>`;
  return `<span class="selo ok" title="Tudo salvo no GitHub"><svg viewBox="0 0 16 16"><path d="M3 8.5l3 3 7-7"/></svg>GitHub</span>`;
}
function seloLigado(p) {
  if (!p.containers && !p.ficha?.links?.teste) return "";
  return p.ligado
    ? `<span class="selo ok">● ligado${p.containers ? ` ${p.containersLigados}/${p.containers}` : ""}</span>`
    : `<span class="selo">○ desligado</span>`;
}

function passaFiltro(p) {
  if (filtro.area && (p.ficha?.area || "") !== filtro.area) return false;
  if (filtro.situacao && p.situacao !== filtro.situacao) return false;
  if (filtro.busca) {
    const alvo = [nome(p), p.nomePasta, p.ficha?.resumo, p.ficha?.tecnologia, p.ficha?.situacao, p.ficha?.anotacoes, p.tecnologias.join(" ")]
      .join(" ").toLowerCase();
    if (!filtro.busca.split(/\s+/).every((t) => alvo.includes(t))) return false;
  }
  return true;
}

function desenhar() {
  const todos = panorama.projetos;
  const ligados = todos.filter((p) => p.ligado).length;
  $("#resumo-topo").textContent =
    `${todos.length} projetos · ${ligados} ligado(s) · ${panorama.dockerDisponivel ? "Docker ligado" : "Docker desligado"} · lido às ${new Date(panorama.geradoEm).toLocaleTimeString("pt-BR", { timeStyle: "short" })}`;

  // chips de situação com contagem (respeitando o filtro de área)
  const naArea = todos.filter((p) => !filtro.area || p.ficha?.area === filtro.area);
  $("#filtro-situacao").innerHTML =
    `<button class="chip ${filtro.situacao ? "" : "ativo"}" data-v="">Todas <b>${naArea.length}</b></button>` +
    Object.entries(SITUACOES).map(([k, s]) =>
      `<button class="chip s-${k} ${filtro.situacao === k ? "ativo" : ""}" data-v="${k}"><span class="ponto"></span>${s.rotulo} <b>${naArea.filter((p) => p.situacao === k).length}</b></button>`).join("");

  desenharAlertas();

  const visiveis = todos.filter(passaFiltro);
  if (!visiveis.length) { $("#lista").innerHTML = `<p class="vazio">Nenhum projeto com esse filtro.</p>`; return; }
  $("#lista").innerHTML = Object.entries(SITUACOES).map(([k, s]) => {
    const doGrupo = visiveis.filter((p) => p.situacao === k).sort((a, b) => (b.ultimaAlteracao || "").localeCompare(a.ultimaAlteracao || ""));
    if (!doGrupo.length) return "";
    return `<section class="grupo"><h3><span class="ponto s-${k}"></span>${s.grupo} · ${doGrupo.length}</h3><div class="grade">${doGrupo.map(cartao).join("")}</div></section>`;
  }).join("");
}

function cartao(p) {
  const f = p.ficha || {};
  return `<article class="cartao s-${p.situacao}" tabindex="0" data-id="${esc(p.id)}">
    <div class="c-cabeca"><span class="ponto" title="${SITUACOES[p.situacao].rotulo}" style="margin-top:6px"></span><h4>${esc(nome(p))}</h4>${f.area ? `<span class="area">${esc(f.area)}</span>` : ""}</div>
    <p class="c-resumo">${esc(f.resumo || (p.erroFicha ? p.erroFicha : "Sem ficha ainda — abra para ver os detalhes."))}</p>
    <div class="c-meta">${seloLigado(p)}${seloGit(p)}${p.tecnologias.slice(0, 3).map((t) => `<span class="selo">${esc(t)}</span>`).join("")}</div>
    <div class="c-rodape">
      <span class="quando" title="${esc(dataHora(p.ultimaAlteracao))}">atualizado ${quando(p.ultimaAlteracao)}</span>
      ${botaoLigarCartao(p)}
      <button class="botao pequeno" data-acao="prompt" title="Copiar o resumo para colar no Claude">Copiar prompt</button>
    </div>
  </article>`;
}

// Desligado e com comando: "Ligar" (liga, espera responder e abre). Ligado ou sem comando: "Abrir".
function botaoLigarCartao(p) {
  const f = p.ficha || {}, t = f.links?.teste;
  if (ligando.has(p.id)) return `<button class="botao pequeno" disabled>Ligando…</button>`;
  if (t && f.ligar && !p.ligado) return `<button class="botao pequeno principal" data-acao="ligar" title="Roda: ${esc(f.ligar)}">Ligar</button>`;
  return t ? `<button class="botao pequeno" data-acao="link" data-link="${esc(t)}" title="${esc(t)}">Abrir</button>` : "";
}

function desenharAlertas() {
  const a = panorama.alertas;
  const sec = $("#alertas");
  if (!a.length) { sec.hidden = true; return; }
  const ordem = { erro: 0, decisao: 1, aviso: 2, info: 3 };
  const importantes = a.filter((x) => x.nivel !== "info").length;
  const nomes = { erro: "erro", decisao: "decidir", aviso: "aviso", info: "info" };
  sec.hidden = false;
  const estavaAberto = sec.querySelector("details")?.open ?? false;
  sec.innerHTML = `<details ${estavaAberto ? "open" : ""}><summary><span>⚠</span>${importantes} alerta(s)${a.length > importantes ? ` · ${a.length - importantes} informação(ões)` : ""}
      <svg class="seta" viewBox="0 0 16 16"><path d="M4 6l4 4 4-4"/></svg></summary>
    <ul>${[...a].sort((x, y) => ordem[x.nivel] - ordem[y.nivel]).map((x) =>
      `<li><span class="nivel ${x.nivel}">${nomes[x.nivel]}</span><span>${esc(x.texto)}</span>${x.projetoId ? `<button class="botao pequeno" data-abrir="${esc(x.projetoId)}">Ver</button>` : ""}</li>`).join("")}</ul></details>`;
}

// ---- prompt para o Claude ----
function montarPrompt(p) {
  const f = p.ficha || {}, g = p.git;
  const estado = [SITUACOES[p.situacao].rotulo];
  if (p.containers) estado.push(p.ligado ? `ligado no Docker (${p.containersLigados}/${p.containers} containers)` : "desligado no Docker");
  else if (f.links?.teste) estado.push(p.ligado ? "ligado localmente" : "desligado");
  if (f.servidor) estado.push("publicado no mini servidor");
  if (!g) estado.push("sem git (só existe nesta pasta)");
  else if (!g.remoto) estado.push(`git local no ramo ${g.ramo}, sem GitHub`);
  else estado.push(`git ${g.pendentes || g.naoEnviados ? `com ${g.pendentes} alteração(ões) não salva(s) e ${g.naoEnviados} commit(s) não enviado(s)` : "em dia com o GitHub"} (ramo ${g.ramo})`);

  const linhas = [
    `Projeto: ${nome(p)}`,
    `Pasta: ${p.pasta}`,
    f.resumo && `O que faz: ${f.resumo}`,
    `Tecnologia: ${f.tecnologia || p.tecnologias.join(", ") || "não identificada"}${p.temClaudeMd ? " — leia o CLAUDE.md da pasta antes de mexer" : p.temReadme ? " — veja o README da pasta" : ""}`,
    f.situacao && `Situação: ${f.situacao.trim()}`,
    `Estado agora: ${estado.join("; ")}`,
    `Última alteração: ${dataHora(p.ultimaAlteracao)}${p.arquivoMaisRecente ? ` (${p.arquivoMaisRecente})` : ""}`,
    g?.ultimoCommit && `Último commit: ${data(g.ultimoCommit.data)} — ${g.ultimoCommit.mensagem}`,
    (f.links?.teste || f.links?.producao) && `Links: ${[f.links.teste && `teste ${f.links.teste}`, f.links.producao && `produção ${f.links.producao}`].filter(Boolean).join(" · ")}`,
    f.anotacoes && `Minhas anotações: ${f.anotacoes.trim()}`,
    "",
    "Quero alterar: ",
  ];
  return linhas.filter((l) => typeof l === "string").join("\n");
}

async function copiarPrompt(p) {
  await chamar("copiar", null, montarPrompt(p));
  aviso("Prompt copiado — cole no Claude e complete o \"Quero alterar\"");
}

// ---- detalhe ----
function controleLigar(p) {
  const f = p.ficha || {}, servico = !!f.links?.teste;
  if (ligando.has(p.id)) return `<button class="botao" disabled>${ligando.get(p.id)}</button>`;
  const ligar = `<button class="botao principal" data-acao="ligar" ${f.ligar ? "" : "disabled"}>${servico ? "Ligar" : "Executar"}</button>`;
  const desligar = `<button class="botao" data-acao="desligar" ${p.ligado && (f.desligar || servico) ? "" : "disabled"}>Desligar</button>`;
  return servico && p.ligado ? desligar : servico ? ligar + desligar : ligar;
}
function abrirDetalhe(id) {
  const p = panorama.projetos.find((x) => x.id === id);
  if (!p) return;
  aberto = id;
  const f = p.ficha || {}, g = p.git;
  $("#d-etiquetas").innerHTML = `<span class="ponto s-${p.situacao}"></span>${SITUACOES[p.situacao].rotulo}${f.area ? ` · ${esc(f.area)}` : ""}`;
  $("#d-nome").textContent = nome(p);
  $("#d-pasta").textContent = p.pasta;

  const links = [f.links?.teste && ["teste", f.links.teste], f.links?.producao && ["produção", f.links.producao]].filter(Boolean);
  $("#d-corpo").innerHTML = `
    <div class="bloco">
      ${f.resumo ? `<p>${esc(f.resumo)}</p>` : `<p class="suave">${esc(p.erroFicha || "Este projeto ainda não tem ficha (hangar.yaml).")}</p>`}
      ${f.situacao ? `<p class="suave" style="white-space:pre-wrap">${esc(f.situacao.trim())}</p>` : ""}
      <div class="acoes" style="margin-top:10px">
        ${links.map(([r, u]) => `<button class="botao principal" data-acao="link" data-link="${esc(u)}" title="${esc(u)}">Abrir ${r}</button>`).join("")}
        <button class="botao" data-acao="pasta">Abrir pasta</button>
        <button class="botao" data-acao="vscode">Abrir no VS Code</button>
        <button class="botao" data-acao="claude" title="Abre o VS Code nesta pasta com o Claude perguntando onde paramos">Abrir Claude aqui</button>
      </div>
    </div>

    <div class="bloco">
      <h5>Prompt para o Claude <small>copie, cole e complete o “Quero alterar”</small></h5>
      <pre class="prompt mono" id="d-prompt">${esc(montarPrompt(p))}</pre>
      <div class="linha-acoes"><button class="botao principal" data-acao="prompt">Copiar prompt</button></div>
    </div>

    <div class="bloco">
      <h5>Anotações <small>salvas na ficha do projeto e incluídas no prompt</small></h5>
      <textarea id="d-anotacoes" placeholder="Ex.: falta o relatório mensal; cliente pediu filtro por representante…">${esc(f.anotacoes || "")}</textarea>
      <div class="linha-acoes"><span class="estado" id="d-anot-estado"></span><button class="botao" data-acao="salvar-anotacoes">Salvar anotações</button></div>
    </div>

    <div class="bloco">
      <h5>Controle</h5>
      <div class="acoes">
        ${controleLigar(p)}
        <button class="botao" disabled>Salvar no GitHub</button><button class="botao" disabled>Arquivar</button>
        <button class="botao" disabled>Publicar no servidor</button><button class="botao" disabled>Retirar do servidor</button>
      </div>
      <p class="fase">${f.ligar ? `Ligar roda <span class="mono">${esc(f.ligar)}</span>${f.desligar ? `; desligar roda <span class="mono">${esc(f.desligar)}</span>` : ""}.` : "A ficha não tem comando para ligar."} Salvar e arquivar chegam na fase 2; publicar e retirar do servidor, na fase 3.</p>
    </div>

    <div class="bloco">
      <h5>Situação técnica</h5>
      <dl class="dados">
        <dt>Última alteração</dt><dd>${esc(dataHora(p.ultimaAlteracao))} ${p.arquivoMaisRecente ? `<span class="suave mono">${esc(p.arquivoMaisRecente)}</span>` : ""}</dd>
        <dt>Tecnologias</dt><dd>${esc(f.tecnologia || p.tecnologias.join(", ") || "—")}</dd>
        <dt>Docker</dt><dd>${p.containers ? `${p.containersLigados} de ${p.containers} container(s) ligado(s)` : panorama.dockerDisponivel ? "sem containers" : "Docker desligado"}</dd>
        <dt>Git</dt><dd>${g ? `ramo <b>${esc(g.ramo)}</b> · ${g.remoto ? esc(g.remoto.replace(/^https:\/\/github.com\//, "GitHub: ")) : "sem GitHub"} · ${g.pendentes} não salva(s) · ${g.naoEnviados} não enviada(s)` : "sem git"}</dd>
        <dt>Guia</dt><dd>${p.temClaudeMd ? "CLAUDE.md" : ""}${p.temClaudeMd && p.temReadme ? " · " : ""}${p.temReadme ? "README" : ""}${!p.temClaudeMd && !p.temReadme ? "—" : ""}</dd>
      </dl>
      ${g?.commits?.length ? `<ul class="commits">${g.commits.map((c) => `<li><span class="mono">${esc(data(c.data))} ${esc(c.hash)}</span><span>${esc(c.mensagem)}</span></li>`).join("")}</ul>` : ""}
    </div>`;
  $("#detalhe").hidden = false;
  document.body.classList.add("travado");
}
function fecharDetalhe() { $("#detalhe").hidden = true; document.body.classList.remove("travado"); aberto = null; }

async function ligarDesligar(p, acao) {
  if (ligando.has(p.id)) return;
  ligando.set(p.id, acao === "ligar" ? "Ligando…" : "Desligando…");
  redesenhar();
  try {
    const r = await chamar(acao, p.id);
    if (p.ficha?.links?.teste) p.ligado = acao === "ligar"; // mostra já; o carregar() abaixo confirma
    if (r.link) await chamar("abrirLink", p.id, r.link);
    aviso(`${nome(p)}: ${r.mensagem.toLowerCase()}`, 3500);
  } catch (err) {
    aviso(`${nome(p)}: ${err.message}`, 9000);
  } finally {
    ligando.delete(p.id);
    redesenhar();
    await carregar();
  }
}
const redesenhar = () => { desenhar(); if (aberto) abrirDetalhe(aberto); };

// ---- eventos ----
const projetoDe = (el) => panorama.projetos.find((x) => x.id === (el.closest("[data-id]")?.dataset.id ?? aberto));

document.addEventListener("click", async (e) => {
  const fechar = e.target.closest("[data-fechar]");
  if (fechar) return fecharDetalhe();
  const abrir = e.target.closest("[data-abrir]");
  if (abrir) return abrirDetalhe(abrir.dataset.abrir);
  const seg = e.target.closest("#filtro-area button");
  if (seg) {
    filtro.area = seg.dataset.v;
    document.querySelectorAll("#filtro-area button").forEach((b) => b.classList.toggle("ativo", b === seg));
    return desenhar();
  }
  const chip = e.target.closest("#filtro-situacao .chip");
  if (chip) { filtro.situacao = chip.dataset.v; return desenhar(); }

  const botao = e.target.closest("[data-acao]");
  if (botao) {
    e.stopPropagation();
    const p = projetoDe(botao);
    if (!p) return;
    try {
      switch (botao.dataset.acao) {
        case "prompt": await copiarPrompt(p); break;
        case "link": await chamar("abrirLink", p.id, botao.dataset.link); if (!p.ligado) aviso("Abrindo… se não carregar, o projeto pode estar desligado"); break;
        case "pasta": await chamar("abrirPasta", p.id); break;
        case "vscode": await chamar("abrirVsCode", p.id); break;
        case "claude":
          await chamar("abrirClaude", p.id, montarPrompt(p));
          aviso("Abrindo o VS Code… o Claude vem com o pedido \"onde paramos\" escrito: é só apertar Enter. O prompt completo também está copiado.", 7000);
          break;
        case "ligar": case "desligar": await ligarDesligar(p, botao.dataset.acao); break;
        case "salvar-anotacoes": {
          const texto = $("#d-anotacoes").value;
          await chamar("salvarAnotacoes", p.id, texto);
          p.ficha = { ...(p.ficha || {}), anotacoes: texto.trim() || null };
          $("#d-prompt").textContent = montarPrompt(p);
          $("#d-anot-estado").textContent = "Salvo " + new Date().toLocaleTimeString("pt-BR", { timeStyle: "short" });
          break;
        }
      }
    } catch (err) { aviso("Erro: " + err.message); }
    return;
  }
  const c = e.target.closest(".cartao");
  if (c) abrirDetalhe(c.dataset.id);
});

document.addEventListener("keydown", (e) => {
  if (e.key === "Escape" && !$("#detalhe").hidden) fecharDetalhe();
  else if (e.key === "Enter" && e.target.classList?.contains("cartao")) abrirDetalhe(e.target.dataset.id);
  else if (e.key === "F5") { e.preventDefault(); carregar(); }
  else if (e.key === "/" && document.activeElement !== $("#busca") && document.activeElement?.tagName !== "TEXTAREA") { e.preventDefault(); $("#busca").focus(); }
});
$("#busca").addEventListener("input", (e) => { filtro.busca = e.target.value.trim().toLowerCase(); if (panorama) desenhar(); });
$("#atualizar").addEventListener("click", carregar);
// Ao voltar para a janela, relê (o estado do Docker/git pode ter mudado lá fora).
let ultimaLeitura = Date.now();
window.addEventListener("focus", () => { if (Date.now() - ultimaLeitura > 60000) { ultimaLeitura = Date.now(); carregar(); } });

carregar();

import { api } from './api.js';
import { el, ehMac, fmt, icone, preencher } from './ui.js';
import { abrirAtalhos, abrirPaleta } from './paleta.js';
import * as busca from './views/busca.js';
import * as indice from './views/indice.js';
import * as fontes from './views/fontes.js';
import * as termos from './views/termos.js';
import * as integracoes from './views/integracoes.js';

const rotas = { busca, indice, fontes, termos, integracoes };
const conteudo = document.getElementById('conteudo');
export const estado = { info: null, indice: null, conectado: true };

function lerTema() {
  try {
    return localStorage.getItem('sonda-tema');
  } catch {
    return null;
  }
}

function gravarTema(valor) {
  try {
    localStorage.setItem('sonda-tema', valor);
  } catch {
    // sem armazenamento: o tema vale só para esta aba
  }
}

function aplicarTema(tema) {
  const escuro = tema === 'escuro' || (!tema && window.matchMedia('(prefers-color-scheme: dark)').matches);
  document.documentElement.dataset.tema = escuro ? 'escuro' : 'claro';
  const botao = document.getElementById('alternar-tema');
  botao.replaceChildren(icone(escuro ? 'sol' : 'lua'));
}

export function alternarTema() {
  const proximo = document.documentElement.dataset.tema === 'escuro' ? 'claro' : 'escuro';
  gravarTema(proximo);
  aplicarTema(proximo);
}

function iconesEstaticos() {
  document.querySelectorAll('[data-icone]').forEach((alvo) => alvo.replaceWith(icone(alvo.dataset.icone)));
}

export async function atualizarSituacao() {
  const regua = document.getElementById('situacao');
  try {
    const [info, indiceAtual] = await Promise.all([api('/info'), api('/indice').catch(() => null)]);
    estado.info = info;
    estado.indice = indiceAtual;
    document.getElementById('versao').textContent = `v${info.versao}`;
    preencher(regua,
      el('span', {}, el('b', { text: fmt.numero(info.documentos) }), ' documentos'),
      el('span', {}, el('b', { text: fmt.numero(info.segmentos) }), info.segmentos === 1 ? ' segmento' : ' segmentos'),
      indiceAtual ? el('span', { class: 'opcional' }, el('b', { text: fmt.bytes(indiceAtual.sizeBytes) }), ' em disco') : null,
      el('span', {}, 'modelo ', el('b', { text: info.similaridade })),
      indiceAtual ? el('span', { class: 'opcional' }, 'gravação ', el('b', { text: `segments_${indiceAtual.generation}` })) : null,
      info.somenteLeitura ? el('span', {}, el('b', { text: 'só leitura' })) : null,
      info.protegido ? el('span', { class: 'opcional' }, 'escrita ', el('b', { text: 'protegida por chave' })) : null,
      el('span', { class: `conexao ${estado.conectado ? '' : 'off'}`, text: estado.conectado ? 'ao vivo' : 'reconectando' }));
  } catch {
    regua.replaceChildren(el('span', { class: 'conexao off', text: 'Servidor indisponível' }));
  }
}

function lerRota() {
  const hash = location.hash.replace(/^#\/?/, '');
  const [caminho, consulta] = hash.split('?');
  const nome = caminho && rotas[caminho] ? caminho : 'busca';
  return { nome, parametros: new URLSearchParams(consulta ?? '') };
}

export function rotaAtual() {
  return lerRota();
}

let limpezaAtual = null;
let ultimaRota = null;

async function renderizar() {
  const { nome, parametros } = lerRota();
  document.querySelectorAll('.nav a').forEach((link) => {
    if (link.dataset.rota === nome) {
      link.setAttribute('aria-current', 'page');
    } else {
      link.removeAttribute('aria-current');
    }
  });
  if (typeof limpezaAtual === 'function') {
    limpezaAtual();
  }
  limpezaAtual = null;
  conteudo.replaceChildren();
  if (ultimaRota !== nome) {
    window.scrollTo(0, 0);
  }
  ultimaRota = nome;
  const titulos = { busca: 'Busca', indice: 'Visão geral', fontes: 'Fontes', termos: 'Termos', integracoes: 'Integrações' };
  const q = parametros.get('q');
  document.title = nome === 'busca' && q ? `${q} · Sonda` : `${titulos[nome]} · Sonda`;
  limpezaAtual = await rotas[nome].render(conteudo, parametros);
}

export function navegar(rota, parametros = {}) {
  const busca = new URLSearchParams();
  for (const [chave, valor] of Object.entries(parametros)) {
    if (valor !== undefined && valor !== null && valor !== '' && valor !== false) {
      busca.set(chave, String(valor));
    }
  }
  const texto = busca.toString();
  const destino = `#/${rota === 'busca' ? '' : rota}${texto ? `?${texto}` : ''}`;
  if (location.hash === destino || (destino === '#/' && location.hash === '')) {
    renderizar();
  } else {
    location.hash = destino;
  }
}

function conectarEventos() {
  if (!('EventSource' in window)) {
    return;
  }
  const fonte = new EventSource('api/eventos');
  fonte.addEventListener('open', () => {
    if (!estado.conectado) {
      estado.conectado = true;
      atualizarSituacao();
    }
  });
  fonte.addEventListener('error', () => {
    if (estado.conectado) {
      estado.conectado = false;
      atualizarSituacao();
    }
  });
  for (const nome of ['progresso', 'fila', 'execucao', 'indice']) {
    fonte.addEventListener(nome, (evento) => {
      const dados = JSON.parse(evento.data);
      window.dispatchEvent(new CustomEvent(`sonda:${nome}`, { detail: dados }));
      if (nome === 'indice' || nome === 'execucao') {
        atualizarSituacao();
      }
    });
  }
}

function digitando(alvo) {
  return alvo instanceof HTMLElement && (alvo.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(alvo.tagName));
}

let prefixoG = 0;

function atalhosGlobais(evento) {
  if ((evento.ctrlKey || evento.metaKey) && evento.key.toLowerCase() === 'k') {
    evento.preventDefault();
    abrirPaleta();
    return;
  }
  if (evento.ctrlKey || evento.metaKey || evento.altKey || digitando(evento.target) || document.querySelector('.paleta-fundo')) {
    return;
  }
  if (evento.key === '?') {
    evento.preventDefault();
    abrirAtalhos();
    return;
  }
  if (evento.key === '/') {
    evento.preventDefault();
    const campo = document.querySelector('.busca-entrada input');
    if (campo) {
      campo.focus();
      campo.select();
    } else {
      navegar('busca');
    }
    return;
  }
  if (evento.key === 'g') {
    prefixoG = Date.now();
    return;
  }
  if (Date.now() - prefixoG < 1200) {
    const destino = { b: 'busca', i: 'indice', f: 'fontes', t: 'termos', n: 'integracoes' }[evento.key];
    prefixoG = 0;
    if (destino) {
      evento.preventDefault();
      navegar(destino);
    }
  }
}

iconesEstaticos();
aplicarTema(lerTema());
document.getElementById('tecla-paleta').textContent = ehMac() ? '⌘ K' : 'Ctrl K';
document.getElementById('alternar-tema').addEventListener('click', alternarTema);
document.getElementById('abrir-paleta').addEventListener('click', () => abrirPaleta());
document.getElementById('abrir-atalhos').addEventListener('click', () => abrirAtalhos());
document.addEventListener('keydown', atalhosGlobais);
window.addEventListener('hashchange', renderizar);
conectarEventos();
atualizarSituacao().finally(renderizar);

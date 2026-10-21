import { api } from './api.js';
import { el, fmt, icone } from './ui.js';
import * as busca from './views/busca.js';
import * as indice from './views/indice.js';
import * as fontes from './views/fontes.js';
import * as termos from './views/termos.js';

const rotas = { busca, indice, fontes, termos };
const conteudo = document.getElementById('conteudo');
export const estado = { info: null };

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
    return;
  }
}

function aplicarTema(tema) {
  const escuro = tema === 'escuro' || (!tema && window.matchMedia('(prefers-color-scheme: dark)').matches);
  document.documentElement.dataset.tema = escuro ? 'escuro' : 'claro';
  const botao = document.getElementById('alternar-tema');
  botao.replaceChildren(icone(escuro ? 'sol' : 'lua'));
}

function iconesEstaticos() {
  document.querySelectorAll('[data-icone]').forEach((alvo) => alvo.replaceWith(icone(alvo.dataset.icone)));
}

export async function atualizarSituacao() {
  try {
    estado.info = await api('/info');
    const situacao = document.getElementById('situacao');
    situacao.replaceChildren(
      el('strong', { text: `${fmt.numero(estado.info.documentos)} documentos` }),
      el('span', { text: `${estado.info.segmentos} segmento(s) · ${estado.info.similaridade}${estado.info.somenteLeitura ? ' · só leitura' : ''}` }));
  } catch {
    document.getElementById('situacao').replaceChildren(el('strong', { text: 'Servidor indisponível' }));
  }
}

function lerRota() {
  const hash = location.hash.replace(/^#\/?/, '');
  const [caminho, consulta] = hash.split('?');
  const nome = caminho && rotas[caminho] ? caminho : 'busca';
  return { nome, parametros: new URLSearchParams(consulta ?? '') };
}

let limpezaAtual = null;

async function renderizar() {
  const { nome, parametros } = lerRota();
  document.querySelectorAll('.nav a').forEach((link) => {
    link.toggleAttribute('aria-current', link.dataset.rota === nome);
    if (link.dataset.rota === nome) {
      link.setAttribute('aria-current', 'page');
    }
  });
  if (typeof limpezaAtual === 'function') {
    limpezaAtual();
  }
  limpezaAtual = null;
  conteudo.replaceChildren();
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
  const destino = `#/${rota}${texto ? `?${texto}` : ''}`;
  if (location.hash === destino) {
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

iconesEstaticos();
aplicarTema(lerTema());
document.getElementById('alternar-tema').addEventListener('click', () => {
  const proximo = document.documentElement.dataset.tema === 'escuro' ? 'claro' : 'escuro';
  gravarTema(proximo);
  aplicarTema(proximo);
});
window.addEventListener('hashchange', renderizar);
conectarEventos();
atualizarSituacao().finally(renderizar);

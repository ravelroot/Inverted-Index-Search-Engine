import { api, definirChave, temChave } from '../api.js';
import { estado } from '../app.js';
import { aviso, cabecalho, carregando, el, fmt, icone, preencher, tabela } from '../ui.js';

const SITUACAO = { concluida: 'Concluída', falhou: 'Falhou', cancelada: 'Cancelada' };

export async function render(alvo) {
  const progresso = el('section', { class: 'painel', hidden: true });
  const listaFontes = el('section', { class: 'painel' }, carregando());
  const historico = el('section', { class: 'painel' }, carregando());
  const formulario = novoFormulario(() => carregar());

  preencher(alvo,
    cabecalho('Índice', 'Fontes', 'Pastas que a Sonda varre e indexa. Arquivos novos, alterados e removidos são detectados a cada execução.'),
    estado.info?.protegido && !temChave() ? campoChave(() => carregar()) : null,
    estado.info?.somenteLeitura ? el('div', { class: 'aviso-relaxado', text: 'O índice está aberto por outro processo, então este painel funciona só para leitura.' }) : null,
    progresso,
    el('div', { class: 'pilha' }, listaFontes, formulario, historico));

  let estadoIndexacao = null;

  async function carregar() {
    try {
      const [fontes, execucoes, situacao] = await Promise.all([api('/fontes'), api('/execucoes?limite=15'), api('/indexacao')]);
      estadoIndexacao = situacao;
      listaFontes.replaceChildren(...painelFontes(fontes, carregar));
      historico.replaceChildren(...painelHistorico(execucoes));
      desenharProgresso();
    } catch (erro) {
      listaFontes.replaceChildren(el('div', { class: 'vazio-painel', text: erro.message }));
    }
  }

  function desenharProgresso(dados) {
    const p = dados ?? estadoIndexacao?.progress;
    if (!estadoIndexacao?.running || !p) {
      progresso.hidden = true;
      return;
    }
    progresso.hidden = false;
    const proporcao = p.discovered > 0 ? p.processed / p.discovered : 0;
    progresso.replaceChildren(
      el('div', { class: 'painel-cabecalho' },
        el('div', {}, el('h2', { text: `Indexando "${p.source}"` }), el('p', { text: `${p.phase} · ${fmt.duracao(p.elapsedSeconds)}` })),
        el('button', {
          class: 'botao secundario pequeno',
          type: 'button',
          text: 'Cancelar',
          onclick: async () => {
            await api('/indexacao/cancelar', { metodo: 'POST' }).catch((e) => aviso(e.message, 'erro'));
          },
        })),
      el('div', { class: 'painel-corpo progresso' },
        el('div', { class: 'barra' }, el('i', { style: { width: `${Math.min(100, proporcao * 100)}%` } })),
        el('div', { class: 'contadores' },
          contador('Encontrados', p.discovered),
          contador('Lidos', p.processed),
          contador('Novos', p.added),
          contador('Atualizados', p.updated),
          contador('Sem mudança', p.unchanged),
          contador('Falhas', p.failed),
          el('div', {}, el('span', { text: 'Vazão' }), el('strong', { text: `${fmt.numero(Math.round(p.filesPerSecond))} arq/s` })),
          el('div', {}, el('span', { text: 'Leitura' }), el('strong', { text: `${fmt.decimal(p.megabytesPerSecond, 1)} MB/s` }))),
        p.currentFile ? el('p', { class: 'arquivo-atual mono', text: p.currentFile }) : null));
  }

  const aoProgresso = (evento) => {
    estadoIndexacao = { ...(estadoIndexacao ?? {}), running: true, progress: evento.detail };
    desenharProgresso(evento.detail);
  };
  const aoMudar = () => carregar();
  window.addEventListener('sonda:progresso', aoProgresso);
  window.addEventListener('sonda:execucao', aoMudar);
  window.addEventListener('sonda:fila', aoMudar);
  await carregar();
  return () => {
    window.removeEventListener('sonda:progresso', aoProgresso);
    window.removeEventListener('sonda:execucao', aoMudar);
    window.removeEventListener('sonda:fila', aoMudar);
  };
}

function contador(rotulo, valor) {
  return el('div', {}, el('span', { text: rotulo }), el('strong', { text: fmt.numero(valor) }));
}

function campoChave(aoSalvar) {
  const entrada = el('input', { type: 'password', autocomplete: 'off', placeholder: 'Chave definida em Sonda:Api:ApiKey' });
  return el('form', {
    class: 'chave-acesso',
    onsubmit: (e) => {
      e.preventDefault();
      definirChave(entrada.value);
      e.target.remove();
      aviso('Chave guardada só nesta aba.');
      aoSalvar();
    },
  },
  el('label', { class: 'campo' }, el('span', { text: 'Chave de acesso' }), entrada, el('small', { text: 'Necessária para indexar, adicionar e remover fontes. Fica só na memória da aba.' })),
  el('button', { class: 'botao', type: 'submit', text: 'Usar chave' }));
}

function painelFontes(fontes, recarregar) {
  const linhas = fontes.map((f) => {
    const ultima = f.lastRun;
    const situacao = f.running ? el('span', { class: 'estado rodando', text: 'Indexando' })
      : f.queued ? el('span', { class: 'estado na-fila', text: 'Na fila' })
        : ultima ? el('span', { class: `estado ${ultima.status}`, text: `${SITUACAO[ultima.status] ?? ultima.status} · ${fmt.dataHora(ultima.finishedAt)}` })
          : el('span', { class: 'mudo', text: 'Nunca indexada' });
    const acao = async (modo) => {
      try {
        await api(`/fontes/${encodeURIComponent(f.name)}/indexar?modo=${modo}`, { metodo: 'POST' });
        aviso(modo === 'completa' ? `Reindexação completa de "${f.name}" na fila.` : `Indexação de "${f.name}" na fila.`);
        recarregar();
      } catch (erro) {
        aviso(erro.message, 'erro');
      }
    };
    return el('tr', {},
      el('td', {}, el('strong', { text: f.name }), f.fromConfiguration ? el('div', { class: 'mudo', style: { fontSize: 'var(--t-xs)' }, text: 'appsettings' }) : null),
      el('td', {}, el('div', { class: 'mono', text: f.path }), f.exists ? null : el('div', { style: { color: 'var(--perigo)', fontSize: 'var(--t-xs)' }, text: 'Pasta não encontrada' })),
      el('td', { class: 'mono mudo', text: f.include.join(' ') }),
      el('td', { class: 'num', text: fmt.numero(f.documents) }),
      el('td', { text: f.watch ? 'Sim' : 'Não' }),
      el('td', {}, situacao),
      el('td', {},
        el('div', { style: { display: 'flex', gap: '4px', justifyContent: 'flex-end' } },
          el('button', { class: 'botao secundario pequeno', type: 'button', text: 'Indexar', disabled: !f.exists, onclick: () => acao('incremental') }),
          el('button', { class: 'botao texto pequeno', type: 'button', text: 'Reindexar tudo', disabled: !f.exists, onclick: () => acao('completa') }),
          f.fromConfiguration ? null : el('button', {
            class: 'botao texto pequeno',
            type: 'button',
            text: 'Remover',
            onclick: async () => {
              try {
                const r = await api(`/fontes/${encodeURIComponent(f.name)}`, { metodo: 'DELETE' });
                aviso(`Fonte removida com ${fmt.numero(r.removidos)} documento(s).`);
                recarregar();
              } catch (erro) {
                aviso(erro.message, 'erro');
              }
            },
          }))));
  });

  return [
    el('div', { class: 'painel-cabecalho' }, el('div', {}, el('h2', { text: 'Pastas indexadas' }), el('p', { text: 'Fontes do appsettings não podem ser removidas pelo painel.' }))),
    tabela(['Nome', 'Pasta', 'Padrões', { texto: 'Documentos', num: true }, 'Observando', 'Última indexação', ''], linhas, { vazio: 'Nenhuma fonte cadastrada.' }),
  ];
}

function novoFormulario(aoCriar) {
  const nome = el('input', { name: 'nome', required: true, pattern: '[a-z0-9_\\-]{1,40}', placeholder: 'documentos' });
  const caminho = el('input', { name: 'caminho', required: true, placeholder: 'C:\\Dados\\Documentos ou /dados/documentos' });
  const incluir = el('input', { name: 'incluir', value: '*.txt, *.md, *.html, *.htm' });
  const excluir = el('input', { name: 'excluir', value: '**/.git/**, **/node_modules/**' });
  const observar = el('input', { type: 'checkbox', name: 'observar', checked: true });
  const lista = (valor) => valor.split(',').map((v) => v.trim()).filter(Boolean);
  const form = el('form', {
    class: 'painel-corpo formulario',
    onsubmit: async (e) => {
      e.preventDefault();
      try {
        await api('/fontes', {
          metodo: 'POST',
          corpo: { nome: nome.value.trim(), caminho: caminho.value.trim(), incluir: lista(incluir.value), excluir: lista(excluir.value), observar: observar.checked },
        });
        aviso(`Fonte "${nome.value.trim()}" adicionada e indexação iniciada.`);
        nome.value = '';
        caminho.value = '';
        aoCriar();
      } catch (erro) {
        aviso(erro.message, 'erro');
      }
    },
  },
  el('label', { class: 'campo' }, el('span', { text: 'Nome' }), nome, el('small', { text: 'Letras minúsculas, números, hífen e sublinhado.' })),
  el('label', { class: 'campo largo' }, el('span', { text: 'Pasta no servidor' }), caminho, el('small', { text: 'Caminho absoluto ou relativo à pasta de onde o servidor foi iniciado.' })),
  el('label', { class: 'campo' }, el('span', { text: 'Incluir' }), incluir),
  el('label', { class: 'campo' }, el('span', { text: 'Excluir' }), excluir),
  el('label', { class: 'marcador' }, observar, 'Reindexar sozinho quando arquivos mudarem'),
  el('div', { class: 'formulario-acoes' }, el('button', { class: 'botao', type: 'submit' }, icone('mais'), 'Adicionar fonte')));

  return el('section', { class: 'painel' },
    el('div', { class: 'painel-cabecalho' }, el('div', {}, el('h2', { text: 'Adicionar fonte' }), el('p', { text: 'Padrões separados por vírgula. ** casa qualquer subpasta.' }))),
    form);
}

function painelHistorico(execucoes) {
  return [
    el('div', { class: 'painel-cabecalho' }, el('div', {}, el('h2', { text: 'Histórico de indexação' }), el('p', { text: 'Últimas execuções, guardadas em dados/catalogo.json.' }))),
    tabela(
      ['Fonte', 'Modo', 'Início', { texto: 'Duração', num: true }, { texto: 'Novos', num: true }, { texto: 'Atualizados', num: true }, { texto: 'Sem mudança', num: true }, { texto: 'Removidos', num: true }, { texto: 'Falhas', num: true }, 'Situação'],
      execucoes.map((r) => el('tr', { title: r.message ?? '' },
        el('td', { text: r.source }),
        el('td', { class: 'mudo', text: r.mode === 'Full' ? 'completa' : 'incremental' }),
        el('td', { class: 'mudo', text: fmt.dataHora(r.startedAt) }),
        el('td', { class: 'num', text: fmt.duracao((new Date(r.finishedAt) - new Date(r.startedAt)) / 1000) }),
        el('td', { class: 'num', text: fmt.numero(r.added) }),
        el('td', { class: 'num', text: fmt.numero(r.updated) }),
        el('td', { class: 'num', text: fmt.numero(r.unchanged) }),
        el('td', { class: 'num', text: fmt.numero(r.deleted) }),
        el('td', { class: 'num', text: fmt.numero(r.failed) }),
        el('td', {}, el('span', { class: `estado ${r.status}`, text: SITUACAO[r.status] ?? r.status })))),
      { vazio: 'Nenhuma indexação ainda.' }),
  ];
}

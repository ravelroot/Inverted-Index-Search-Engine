# Indexação paralela em .NET

Indexar milhares de arquivos é um trabalho que se divide bem entre vários núcleos do processador. Ler o arquivo, extrair o texto, tokenizar e aplicar o stemming são etapas independentes para cada documento.

## Produtor e consumidores

Uma tarefa produtora percorre as pastas e coloca cada arquivo encontrado em um Channel com capacidade limitada. Vários consumidores, criados com Parallel.ForEachAsync da Task Parallel Library, retiram os arquivos do canal e fazem a análise. Se os consumidores ficam lentos, o canal enche e o produtor espera, o que evita estourar a memória.

## Dicionário concorrente

Os termos analisados vão para um ConcurrentDictionary compartilhado, em que cada termo aponta para um buffer de postagens. Duas threads só disputam o mesmo bloqueio quando adicionam o mesmo termo ao mesmo tempo, o que é raro fora das palavras mais comuns.

## Troca de buffer sem pausa

Quando a memória usada passa do limite configurado, o buffer atual é congelado e substituído por um novo. As threads continuam indexando no buffer novo enquanto o antigo é gravado no disco como um segmento. A troca leva microssegundos, protegida por um ReaderWriterLockSlim.

# Compressão das listas de postagens

Em um índice com milhões de documentos, as listas de postagens dos termos comuns têm milhões de entradas. Guardar cada identificador em 32 bits desperdiçaria espaço e, principalmente, tempo de leitura do disco.

## Codificação de tamanho variável

O VInt guarda números pequenos em poucos bytes. Cada byte usa sete bits para o valor e um bit para indicar se ainda há mais bytes. Um número até 127 ocupa um byte; até 16.383, dois bytes.

## Empacotamento em blocos

Para listas longas, os identificadores são agrupados em blocos de 128. O motor calcula quantos bits o maior valor do bloco precisa e grava todos os 128 valores com essa mesma largura. Se as diferenças cabem em 5 bits, o bloco inteiro ocupa 80 bytes em vez de 512.

## Tabela de saltos

Junto com os blocos, o índice grava uma tabela de saltos com o último documento de cada bloco e a posição do bloco no arquivo. Para avançar até o documento 1.000.000, o leitor faz uma busca binária nessa tabela e pula direto para o bloco certo, sem descompactar os anteriores.

A tabela também guarda a maior frequência e o menor tamanho de campo de cada bloco. Com isso o motor sabe a pontuação máxima possível dentro do bloco e pode ignorar trechos inteiros que não têm chance de entrar no top 10.

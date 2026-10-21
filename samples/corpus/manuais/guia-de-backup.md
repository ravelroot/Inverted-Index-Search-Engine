# Guia de backup 3-2-1

A regra 3-2-1 é a forma mais simples de proteger arquivos importantes contra perda.

- **3 cópias** dos dados: o original e mais duas.
- **2 mídias diferentes**, por exemplo o disco do computador e um HD externo.
- **1 cópia fora do local**, em outro prédio ou em um serviço de armazenamento em nuvem.

## Teste a restauração

Um backup só vale se puder ser restaurado. Pelo menos uma vez por mês, escolha alguns arquivos ao acaso e restaure em uma pasta separada para conferir se estão íntegros.

## Versões

Mantenha versões antigas por algumas semanas. Se um arquivo for corrompido ou criptografado por um vírus, a cópia mais recente também pode estar comprometida, e só uma versão anterior resolve.

## Verificação de integridade

Ferramentas de backup confiáveis calculam um checksum, como CRC32 ou SHA-256, de cada arquivo copiado. Se um único bit mudar, o checksum muda e o problema é detectado antes de você precisar da cópia.

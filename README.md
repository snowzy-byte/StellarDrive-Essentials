# StellarDrive Essentials

Special thanks for all that send suggestions on discord server.
Mais possibilidades para construir, automatizar e pilotar no **StellarDrive**.

O StellarDrive Essentials reúne sensores, temporizadores, transmissão de sinais entre naves e melhorias de qualidade de vida. As novas peças ficam organizadas na aba **Essentials** do menu de construção.

**Autor:** Ghost  
**Versão atual:** 1.0.1  
**Estado:** em desenvolvimento

## Funcionalidades

### Sensor de distância

O **Distance Sensor** mede a distância na direção da seta e envia o resultado por sinal.

- Alcance de até **10 km**.
- Visor e saída em **metros**.
- Detecta terreno e obstáculos com colisores carregados.

### Altímetro

O **Altimeter** informa a altitude em relação ao **nível do mar do planeta**, com visor e saída de sinal em metros.

Quando a medição estiver indisponível, o visor mostra `--` e a saída envia **-1**.

### LIDAR de longo alcance

O **LIDAR Sensor** foi desenvolvido para medir até **1.000 km** na direção da lente e do feixe vermelho, com um modelo próprio e controle por sinal.

| Socket | Função |
| --- | --- |
| **OUT** | Distância em metros inteiros. Envia **-1** quando desligado ou sem alvo. |
| **ENABLE** | Liga com sinal positivo. Zero, valor negativo ou ausência de cabo desligam o sensor. |

**Esta peça ainda está em testes.** A validação das leituras no jogo permanece pendente antes do lançamento.

### Timer

O **Signal Timer** mantém o último sinal diferente de zero por um tempo configurável depois que a entrada volta a zero.

| Socket | Função |
| --- | --- |
| **IN** | Sinal que será mantido. |
| **TIME** | Duração em segundos. Conecte uma Constant para definir o tempo.    |
| **OUT** | Saída do sinal. |

Enquanto **IN** recebe um valor diferente de zero, **OUT** acompanha esse valor. Quando a entrada volta a zero, a saída mantém o último valor até o tempo terminar. Um novo sinal atualiza o valor e renova o tempo de retenção. Valores negativos também são aceitos.

### Delay

O **Signal Delay** atrasa todas as mudanças da entrada, incluindo pulsos curtos, valores negativos e o retorno a zero.

Usa os mesmos sockets **IN**, **TIME** e **OUT** do Timer. Com **TIME = 3**, um pulso de um segundo aparece na saída três segundos depois, mantendo sua duração.

Pra evitar consumo excessivo de memória, cada Delay guarda até **65.536 mudanças pendentes**. Se lotar, a saída fica em **0**, o visor mostra `-- s` e aparece um aviso no log. Mude **TIME** ou pegue a peça pra reiniciar.

### Transmissor de sinal entre naves

O **Intership Signal Transmitter** permite conectar circuitos de naves diferentes sem um cabo físico entre elas.

1. Coloque um transmissor em cada nave.
2. Configure o mesmo nome de canal nos transmissores.
3. Conecte os circuitos aos sockets das antenas.

### Portas com entrada de sinal

As portas recebem sockets nos cantos inferiores dos dois lados, permitindo acionamento por circuitos e interação manual.

| Sinal | Comportamento |
| --- | --- |
| **Positivo** | Abre a porta. |
| **Negativo** | Fecha a porta. |
| **Zero** | Neutro: não envia comando para abrir ou fechar. |

### Portas centralizadas em construções de três blocos

O encaixe lateral de **meio bloco** permite centralizar as portas compatíveis de dois blocos em uma estrutura de três blocos de largura.

As faixas laterais são fechadas automaticamente com acabamento de parede metálica após colocar a porta. 

### HUD de velocidade para pilotos

Um indicador no canto superior direito mostra a velocidade da nave em **m/s inteiros**, acompanhado de uma régua dinâmica.

- Aparece enquanto o jogador está sentado no assento de piloto básico ou avançado.
- Desaparece ao sair do assento.

## Requisitos

- **StellarDrive Demo**.
- **MelonLoader** instalado no jogo.

A partir da versão **0.11.0**, o Essentials não precisa mais do SDModKit.

## Reportar problemas

Ao abrir uma **Issue**, informe a versão do mod, a versão do MelonLoader e os passos para reproduzir o problema. Inclua uma imagem ou vídeo quando ajudar a explicar o comportamento e o arquivo **MelonLoader/Latest.log** da sessão em que ocorreu a falha.

## Créditos

- **Ghost** — desenvolvedor do StellarDrive Essentials.
- **Krunk** — StellarDrive Mod Kit, usado nas versões anteriores do mod.
- Comunidade do StellarDrive — sugestões.

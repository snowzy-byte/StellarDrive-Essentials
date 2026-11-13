# StellarDrive Essentials

[English below](#english)

Special thanks for all that send suggestions on discord server.

O StellarDrive Essentials reúne sensores, temporizadores, transmissão de sinais entre naves e melhorias de qualidade de vida. As novas peças ficam organizadas na aba **Essentials** do menu de construção.

**Autor:** Ghost  
**Versão atual:** 1.0.0  

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

## Pesquisas e custos

| Peça | Pesquisa | Custo de construção |
| --- | --- | --- |
| Distance Sensor | Sensors | 3 Iron + 2 Copper |
| Altimeter | Sensors | 3 Iron + 2 Copper |
| Signal Timer | Advanced Signal Processing | 2 Iron + 2 Copper + 1 Glass |
| Signal Delay | Advanced Signal Processing | 2 Iron + 2 Copper |
| Intership Signal Transmitter | Wireless Signals | 5 Iron + 5 Copper |
| LIDAR Sensor | Sensors | 8 Iron + 6 Copper + 1 Stellar Shard |

## Requisitos

- **StellarDrive Demo**.
- **MelonLoader** instalado no jogo.

## Reportar problemas

Se encontrar algum bug, pode me chamar no Discord que eu corrijo o mais rápido que conseguir.

Discord: **benmals0_0**

## Créditos
- Comunidade do StellarDrive — sugestões.

---

## English

Special thanks to everyone who sent suggestions on the Discord server.

StellarDrive Essentials brings together sensors, timers, signals between ships and quality-of-life improvements. The new parts are grouped in the **Essentials** build-menu tab.

**Author:** Ghost  
**Current version:** 1.0.0  

### Features

#### Distance sensor

The **Distance Sensor** measures distance in the direction of its arrow and sends the reading through its signal socket.

- Range of up to **10 km**.
- Display and output in **meters**.
- Detects terrain and obstacles with loaded colliders.

#### Altimeter

The **Altimeter** measures altitude above the planet's **sea level**, with a display and signal output in meters.

When a reading is unavailable, the display shows `--` and the output sends **-1**.

#### Long-range LIDAR

The **LIDAR Sensor** is designed to measure up to **1,000 km** in the direction of its lens and red beam. It has its own model and can be switched on or off through a signal.

| Socket | Function |
| --- | --- |
| **OUT** | Distance in whole meters. Sends **-1** when disabled or when no target is detected. |
| **ENABLE** | A positive signal enables the sensor. Zero, negative values or no cable disable it. |

**This part is still being tested.** In-game reading validation remains pending.

#### Timer

The **Signal Timer** holds the last nonzero signal for a configurable time after the input returns to zero.

| Socket | Function |
| --- | --- |
| **IN** | Signal to hold. |
| **TIME** | Duration in seconds. Connect a Constant to set the time. |
| **OUT** | Signal output. |

While **IN** receives a nonzero value, **OUT** follows it. Once the input returns to zero, the output holds the last value until the time runs out. A new signal updates the value and renews the hold time. Negative values work too.

#### Delay

The **Signal Delay** delays every input change, including short pulses, negative values and the return to zero.

It uses the same **IN**, **TIME** and **OUT** sockets as the Timer. With **TIME = 3**, a one-second pulse appears at the output three seconds later and keeps its original duration.

To keep memory use under control, each Delay can store up to **65,536 pending changes**. If the queue fills up, the output becomes **0**, the display shows `-- s` and a warning is written to the log. Change **TIME** or pick up the part to reset it.

#### Signals between ships

The **Intership Signal Transmitter** connects circuits on different ships without a physical cable between them.

1. Place a transmitter on each ship.
2. Set the same channel name on both transmitters.
3. Connect the circuits to the antennas' sockets.

#### Signal-controlled doors

Doors get sockets in the lower corners on both sides, so you can use circuits and still operate them manually.

| Signal | Behavior |
| --- | --- |
| **Positive** | Opens the door. |
| **Negative** | Closes the door. |
| **Zero** | Neutral: sends no open or close command. |

#### Centered doors in three-block-wide builds

The **half-block** sideways snap lets you center compatible two-block doors in a three-block-wide structure.

The side gaps are automatically filled with metal wall panels after you place the door.

#### Pilot speed HUD

A readout in the upper-right corner shows the ship's speed in **whole m/s**, with a dynamic scale.

- Appears while you are sitting in either the basic or advanced pilot seat.
- Disappears when you leave the seat.

### Research and costs

| Part | Research | Building cost |
| --- | --- | --- |
| Distance Sensor | Sensors | 3 Iron + 2 Copper |
| Altimeter | Sensors | 3 Iron + 2 Copper |
| Signal Timer | Advanced Signal Processing | 2 Iron + 2 Copper + 1 Glass |
| Signal Delay | Advanced Signal Processing | 2 Iron + 2 Copper |
| Intership Signal Transmitter | Wireless Signals | 5 Iron + 5 Copper |
| LIDAR Sensor | Sensors | 8 Iron + 6 Copper + 1 Stellar Shard |

### Requirements

- **StellarDrive Demo**.
- **MelonLoader** installed in the game.

### Report a problem

If you find a bug, send me a message on Discord and I will fix it as soon as I can.

Discord: **benmals0_0**

### Credits

- StellarDrive community — suggestions.

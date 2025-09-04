# Morbot15

Bot de trading para cTrader diseñado para operar el activo US100 usando la estrategia de breakout and retest de distintos niveles clave como lo son el opening range high/low de 15m, el previous day high/low y el premarket high/low.

## Parámetros

### Risk Per Trade (%)

Indica el porcentaje de la cuenta que se quiere arriesgar en cada trade.

### SL Offset (Pips)

El SL siempre se colocará por encima o por debajo de la vela que creó la señal de entrada + el valor en pips que se indique en este parámetro.

### TP Distance (R)

Sirve para especificar cuantas R se desea para el TP.

### Market Structure Filter

Analiza la estructura de mercado para ver si hay una secuencia de HH/HL o LH/LL para decidir si tomar la señal de entrada o ignorarla. El filtro se activa sólo si el nivel de volatilidad no es favorable.

* **Disabled:** No activa el filtro.
* **Fixed:** Activa el filtro si la volatilidad está por debajo de un valor absoluto.
* **Auto:** Compara la volatilidad actual con su propio promedio histórico.

### DD Threshold (%)

Representa el porcentaje de DD al que debe llegar la cuenta para modificar el porcentaje de riesgo por trade. Si se deja vacío o en 0, esta funcionalidad se desactiva.

### Defensive Risk (%)

Es el porcentaje de riesgo que se usa por trade una vez la cuenta alcanza el umbral de DD especificado en el parámetro anterior. También se usa este porcentaje si el Market Structure Filter determina que la volatilidad no es favorable.

### RR Tracks

Se usa para hacer trailing stop de la siguiente forma: Se ingresa una serie de números separados por coma, de los cuales, el primer número será la cantidad de R a la que el trade debe llegar para mover el SL a la cantidad de R que indique el segundo número y asi sucesivamente.
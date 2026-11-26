# Registro de cambios

## [1.10.0]

Actualizado para Crimson Desert 2.06.00.

### Novedades

- **SaveUltimate ahora es CDSE** (Crimson Desert Save Editor), con logotipo, colores y fuentes nuevos. El tema predeterminado es CDSE Obsidiana; el anterior sigue disponible como Carbón y oro. Al actualizar desde 1.9.0, el archivo del programa se sigue llamando `SaveUltimate.exe` hasta la siguiente actualización, que lo cambia a `CDSE.exe`. Los códigos de error empiezan ahora por `CDSE-` en lugar de `SU-`; los números no cambian.
- **Historial:** cada cambio aparece a la derecha. Haz clic para volver a ese momento o avanzar de nuevo (Ctrl+Y). Guardar lo conserva.
- **Objetos → Base de datos de objetos:** añade objetos corrientes al inventario, de un tipo o de varios a la vez. Primero se ven las ranuras que ocupan; no se pueden añadir objetos de misión, llaves ni documentos.
- **Inventario:** al subir la cantidad de un objeto que no se apila, como el equipo, se añaden otras tantas copias con la misma mejora, engarces y tinte.
- **Equipo:** cada engarce muestra su equipo con imagen, como el de las mascotas, y el selector solo lista el equipo del Abismo compatible, cada uno con lo que hace: el valor de un equipo de atributo (Ataque +2, Velocidad de movimiento +4 %) o el efecto de uno de habilidad, y la descripción del juego al pasar el ratón. Las listas de objetos ya no tienen la columna Ranura.
- **Nuevo menú Mascotas y monturas:** añade y elimina mascotas, caballos, monturas especiales, carros y veleros. Un velero llega con su vela y su cañón. Al eliminar una mascota o montura también se borra su equipo. Si algo no se puede eliminar, se muestra el motivo: es el principal, lo nombra una misión o lleva carga.
- **Mascotas y monturas → Equipo:** cambia el equipo de mascotas, caballos, monturas especiales, veleros y A.T.A.G., incluidos los cañones de barco y las armas de A.T.A.G. Se crea nuevo y reemplaza lo que llevaba.
- **Personaje → Tinte:** mira el color, tinte, material y suciedad de cada parte. Exporta una selección o el conjunto de Kliff, Oongka o Damiane a un archivo o como código para pegar en un mensaje, e importa cualquiera de los dos. En cada entrada, elige cuál de tus objetos recibe el tinte, con dónde está, su mejora y su tinte actual, o crea uno nuevo ya teñido.
- **Vida → Vivienda:** gira 180° toda una casa de construcción libre.

### Correcciones

- Al seleccionar una celda ya no se corta su texto.
- Conocimiento: el nivel ya no queda vacío al aplicarlo.
- Todos los mercenarios muestran un retrato del rostro encuadrado como en la lista de mercenarios del juego; los 46 que no tenían imagen ya la tienen.
- Habilidades: Pesca de objetos ya no aparece; parece que aún no está en el juego.

### Otros

- Nuevo menú Personaje con equipo, habilidades y tinte.
- Las imágenes de mascotas, monturas y peces son más grandes, sin fondo y distintas por color.
- Mascotas y monturas ya no muestra ganado, peces capturados ni datos internos.
- Menos botones, dos por fila; lo poco frecuente está en el menú ⋯.
- Todas las ventanas usan la barra de título del editor.
- Las imágenes del juego sincronizadas ocupan alrededor de un tercio del espacio en disco (590 MB → 195 MB) y la sincronización es más rápida. El primer inicio tras actualizar vuelve a sincronizar una vez.

## [1.9.0]

### Novedades

- **Vida → Vivienda:** activa las parcelas del DLC y exporta o importa casas.
- **PNJ → Mascotas y monturas:** reordena los barcos.
- **Francés, alemán, español y portugués de Brasil.**
- Las notas de actualización aparecen en el idioma del editor.

### Correcciones

- Conocimiento: «Aprendido por» mostraba «Sin registro» justo después de abrir una partida.
- Restaurar una copia ahora también deja una copia en la carpeta `backups/` del editor.

### Otros

- La ayuda de las pestañas es más clara. La ayuda de Copia/Restauración indica que cada partida conserva sus 10 copias más recientes.

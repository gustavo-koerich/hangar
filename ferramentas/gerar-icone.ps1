# Gera src\Hangar\hangar.ico (16/32/48/256 px, PNG dentro do ICO). Rode só se quiser mudar o desenho.
Add-Type -AssemblyName System.Drawing
$destino = Join-Path $PSScriptRoot '..\src\Hangar\hangar.ico'

function Desenhar([int]$t) {
  $bmp = New-Object System.Drawing.Bitmap $t, $t
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.Clear([System.Drawing.Color]::Transparent)
  $k = $t / 32.0
  # fundo arredondado azul (Fluent)
  $r = 7 * $k; $caminho = New-Object System.Drawing.Drawing2D.GraphicsPath
  $caminho.AddArc(1*$k, 1*$k, 2*$r, 2*$r, 180, 90); $caminho.AddArc(31*$k - 2*$r, 1*$k, 2*$r, 2*$r, 270, 90)
  $caminho.AddArc(31*$k - 2*$r, 31*$k - 2*$r, 2*$r, 2*$r, 0, 90); $caminho.AddArc(1*$k, 31*$k - 2*$r, 2*$r, 2*$r, 90, 90); $caminho.CloseFigure()
  $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($t, $t)), ([System.Drawing.Color]::FromArgb(0, 120, 212)), ([System.Drawing.Color]::FromArgb(0, 62, 146))
  $g.FillPath($grad, $caminho)
  # hangar: telhado em arco + porta com faixas
  $branco = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
  $teto = New-Object System.Drawing.Drawing2D.GraphicsPath
  $teto.AddArc(5*$k, 9*$k, 22*$k, 16*$k, 180, 180); $teto.AddLine(27*$k, 17*$k, 27*$k, 25*$k); $teto.AddLine(27*$k, 25*$k, 5*$k, 25*$k); $teto.CloseFigure()
  $g.FillPath($branco, $teto)
  $porta = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 84, 166))
  $g.FillRectangle($porta, 10*$k, 15.5*$k, 12*$k, 9.5*$k)
  if ($t -ge 32) { $p = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, 255, 255, 255)), ([Math]::Max(1, 1.1*$k)); foreach ($y in 18.5, 21.5) { $g.DrawLine($p, 10*$k, $y*$k, 22*$k, $y*$k) } }
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  return ,$ms.ToArray()
}

$tamanhos = 16, 32, 48, 256
$imagens = $tamanhos | ForEach-Object { ,(Desenhar $_) }
$saida = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter $saida
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$tamanhos.Count)
$offset = 6 + 16 * $tamanhos.Count
for ($i = 0; $i -lt $tamanhos.Count; $i++) {
  $t = $tamanhos[$i]; $b = if ($t -ge 256) { 0 } else { $t }
  $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$imagens[$i].Length); $w.Write([uint32]$offset)
  $offset += $imagens[$i].Length
}
foreach ($img in $imagens) { $w.Write($img) }
[System.IO.File]::WriteAllBytes((Resolve-Path (Split-Path $destino)).Path + '\hangar.ico', $saida.ToArray())
Write-Host "ícone gerado: $destino"

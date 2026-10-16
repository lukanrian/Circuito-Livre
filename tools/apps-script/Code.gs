/**
 * RafaelElectrical — receptor de "Reportar erro / sugerir função".
 * Publicado como App da Web (executa como o dono, acesso: qualquer pessoa).
 * Recebe JSON do plugin e envia um e-mail ao dono do script com o print anexado.
 * O e-mail de destino não aparece no código: é o da conta que publicou o script.
 */
var MAX_IMAGE_BYTES = 6 * 1024 * 1024;

function doPost(e) {
  try {
    var d = JSON.parse(e.postData.contents);
    var tipo = clean(d.tipo, 40) || 'Erro';
    var assunto = '[RafaelElectrical] ' + tipo + ': ' + (clean(d.comando, 80) || 'sem comando');
    var corpo = [
      'Tipo: ' + tipo,
      'Comando/função: ' + clean(d.comando, 200),
      'Versão do Revit: ' + clean(d.revit, 200),
      'Versão do plugin: ' + clean(d.plugin, 50),
      'Windows: ' + clean(d.windows, 200),
      'Contato: ' + clean(d.contato, 200),
      '',
      'Descrição:',
      clean(d.descricao, 10000)
    ].join('\n');

    var anexos = [];
    if (d.imagem) {
      var bytes = Utilities.base64Decode(d.imagem);
      if (bytes.length <= MAX_IMAGE_BYTES) {
        var tipoImg = /^image\/(png|jpeg|gif|bmp)$/.test(d.imagemTipo) ? d.imagemTipo : 'image/png';
        anexos.push(Utilities.newBlob(bytes, tipoImg, clean(d.imagemNome, 100) || 'print.png'));
      } else {
        corpo += '\n\n(Imagem ignorada: maior que 6 MB.)';
      }
    }

    MailApp.sendEmail({
      to: Session.getEffectiveUser().getEmail(),
      subject: assunto,
      body: corpo,
      attachments: anexos
    });
    return out({ ok: true });
  } catch (err) {
    return out({ ok: false, erro: String(err) });
  }
}

function doGet() {
  return out({ ok: true, servico: 'RafaelElectrical - reportar erro' });
}

function clean(v, max) {
  if (v === undefined || v === null) return '';
  v = String(v);
  return v.length > max ? v.substring(0, max) + '…' : v;
}

function out(obj) {
  return ContentService.createTextOutput(JSON.stringify(obj)).setMimeType(ContentService.MimeType.JSON);
}

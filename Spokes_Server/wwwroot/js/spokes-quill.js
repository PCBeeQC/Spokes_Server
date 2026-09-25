window.spokesQuill = {
    editors: {},
    init: function (elementId, content, dotNetHelper, readOnly, placeholder) {
        const container = document.getElementById(elementId);
        if (!container) return;

        // Ensure quill, blot formatter, and magic url are loaded
        if (typeof Quill === 'undefined') {
            console.error('Quill is not loaded');
            return;
        }

        if (!Quill.imports['modules/imageResize']) {
            const Resizer = window.ResizeQuillImage || window.ImageResize || window.resizeQuillImage;
            if (typeof Resizer !== 'undefined') {
                Quill.register('modules/imageResize', Resizer.default || Resizer);
            }
        }
        
        const toolbarOptions = [
            ['bold', 'italic', 'underline', 'strike'],
            ['blockquote', 'code-block'],
            [{ 'header': 1 }, { 'header': 2 }],
            [{ 'header': [1, 2, 3, 4, 5, 6, false] }],
            [{ 'list': 'ordered'}, { 'list': 'bullet' }],
            [{ 'indent': '-1'}, { 'indent': '+1' }],
            [{ 'color': [] }, { 'background': [] }],
            [{ 'align': [] }],
            ['link', 'image', 'video'],
            ['clean']
        ];

        const hasImageResize = !!Quill.imports['modules/imageResize'];

        const config = {
            theme: 'snow',
            modules: {
                toolbar: toolbarOptions,
                imageResize: hasImageResize ? {} : false,
                magicUrl: typeof window.quillMagicUrl !== 'undefined' || true
            },
            readOnly: readOnly,
            placeholder: placeholder || ''
        };

        const quill = new Quill(container, config);
        
        if (content) {
            const delta = quill.clipboard.convert({ html: content });
            quill.setContents(delta, 'silent');
        }

        quill.on('text-change', function(delta, oldDelta, source) {
            if (source === 'user') {
                let html = container.querySelector('.ql-editor').innerHTML;
                if (html === '<p><br></p>') {
                    html = ''; // Normalize empty state
                }
                dotNetHelper.invokeMethodAsync('OnContentChanged', html);
            }
        });

        this.editors[elementId] = quill;
    },
    getHtml: function (elementId) {
        const quill = this.editors[elementId];
        if (quill) {
            const html = quill.getSemanticHTML ? quill.getSemanticHTML() : quill.root.innerHTML;
            return !html || html === '<p><br></p>' ? '' : html;
        }
        return '';
    },
    setHtml: function (elementId, html) {
        const quill = this.editors[elementId];
        if (quill) {
            const delta = quill.clipboard.convert({ html: html || '' });
            quill.setContents(delta, 'silent');
        }
    },
    insertHtml: function (elementId, html) {
        const quill = this.editors[elementId];
        if (quill) {
            const range = quill.getSelection(true);
            const index = range ? range.index : quill.getLength();
            const delta = quill.clipboard.convert({ html: html });
            const Delta = Quill.import('delta');
            const insertion = new Delta().retain(index).concat(delta);
            quill.updateContents(insertion, 'user');
        }
    },
    clear: function (elementId) {
        const quill = this.editors[elementId];
        if (quill) {
            quill.setText('');
        }
    },
    destroy: function (elementId) {
        const quill = this.editors[elementId];
        if (quill) {
            // Remove DOM and event listeners conceptually
            delete this.editors[elementId];
        }
    }
};

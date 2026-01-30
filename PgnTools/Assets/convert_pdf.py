import os
import time
import logging
from pathlib import Path
from docling.document_converter import DocumentConverter, PdfFormatOption
from docling.datamodel.base_models import InputFormat
from docling.datamodel.pipeline_options import PdfPipelineOptions, TableFormerMode

# --- CONFIGURATION ---
SOURCE_DIR = "pdf"      # Folder containing your downloaded Microsoft PDFs
OUTPUT_DIR = "md_source" # Folder where clean Markdown will go
# ---------------------

def setup_converter():
    """
    Configures Docling to be aggressive about table detection and 
    code block preservation, which is critical for .NET reference docs.
    """
    pipeline_options = PdfPipelineOptions(do_table_structure=True)
    # 'ACCURATE' mode is slower but essential for complex property tables in WinUI docs
    pipeline_options.table_structure_options.mode = TableFormerMode.ACCURATE 

    return DocumentConverter(
        format_options={
            InputFormat.PDF: PdfFormatOption(pipeline_options=pipeline_options)
        }
    )

def batch_convert():
    # 1. Setup
    logging.basicConfig(level=logging.INFO)
    logger = logging.getLogger(__name__)
    
    source_path = Path(SOURCE_DIR)
    output_path = Path(OUTPUT_DIR)
    output_path.mkdir(parents=True, exist_ok=True)

    converter = setup_converter()
    
    # 2. Find all PDFs (recursively)
    pdf_files = list(source_path.rglob("*.pdf"))
    logger.info(f"Found {len(pdf_files)} PDF files to process.")

    # 3. Process Loop
    for i, pdf_file in enumerate(pdf_files, 1):
        try:
            logger.info(f"[{i}/{len(pdf_files)}] Converting: {pdf_file.name}...")
            start_time = time.time()
            
            # The Heavy Lifting
            result = converter.convert(pdf_file)
            markdown_content = result.document.export_to_markdown()

            # 4. Save to Markdown
            # We mirror the filename but change extension to .md
            relative_path = pdf_file.relative_to(source_path)
            dest_file = output_path / relative_path.with_suffix(".md")
            
            # Ensure subdirectories exist in output
            dest_file.parent.mkdir(parents=True, exist_ok=True)

            with open(dest_file, "w", encoding="utf-8") as f:
                f.write(f"\n\n")
                f.write(markdown_content)

            elapsed = time.time() - start_time
            logger.info(f"✓ Finished {pdf_file.name} in {elapsed:.2f}s")

        except Exception as e:
            logger.error(f"❌ FAILED {pdf_file.name}: {e}")

if __name__ == "__main__":
    batch_convert()
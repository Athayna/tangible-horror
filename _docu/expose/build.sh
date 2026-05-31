#!/bin/bash

# Build script for LaTeX with biblatex/biber
echo "Building expose with biblatex..."

# First LaTeX run
echo "First pdflatex run..."
pdflatex -interaction=nonstopmode expose.tex

# Run biber
echo "Running biber..."
biber expose

# Second LaTeX run to incorporate bibliography
echo "Second pdflatex run..."
pdflatex -interaction=nonstopmode expose.tex

# Third LaTeX run to resolve references
echo "Third pdflatex run..."
pdflatex -interaction=nonstopmode expose.tex

echo "Build completed!"
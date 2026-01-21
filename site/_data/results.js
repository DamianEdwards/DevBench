const fs = require('fs');
const path = require('path');

module.exports = function() {
  const resultsDir = process.env.RESULTS_DIR || path.join(__dirname, '..', '..', 'results');
  const results = [];
  
  if (!fs.existsSync(resultsDir)) {
    console.warn(`Results directory not found: ${resultsDir}`);
    return results;
  }
  
  const files = fs.readdirSync(resultsDir).filter(f => f.endsWith('.json'));
  
  for (const file of files) {
    try {
      const content = fs.readFileSync(path.join(resultsDir, file), 'utf-8');
      const data = JSON.parse(content);
      results.push({
        ...data,
        fileName: file
      });
    } catch (err) {
      console.warn(`Failed to parse ${file}: ${err.message}`);
    }
  }
  
  // Sort by timestamp descending
  results.sort((a, b) => new Date(b.timestamp) - new Date(a.timestamp));
  
  return results;
};

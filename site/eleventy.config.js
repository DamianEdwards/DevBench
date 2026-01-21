const fs = require('fs');
const path = require('path');

module.exports = function(eleventyConfig) {
  // Copy static assets
  eleventyConfig.addPassthroughCopy("css");
  eleventyConfig.addPassthroughCopy("icons");
  
  // Watch for changes
  eleventyConfig.addWatchTarget("./css/");
  eleventyConfig.addWatchTarget("../results/");
  
  // Add filters
  eleventyConfig.addFilter("formatDate", (dateString) => {
    const date = new Date(dateString);
    return date.toLocaleDateString('en-US', { 
      year: 'numeric', 
      month: 'short', 
      day: 'numeric' 
    });
  });
  
  eleventyConfig.addFilter("formatMs", (ms) => {
    if (ms < 1000) return `${ms}ms`;
    return `${(ms / 1000).toFixed(2)}s`;
  });
  
  eleventyConfig.addFilter("round", (num, decimals = 0) => {
    return Number(num).toFixed(decimals);
  });

  return {
    dir: {
      input: ".",
      includes: "_includes",
      data: "_data",
      output: "_site"
    },
    markdownTemplateEngine: "njk",
    htmlTemplateEngine: "njk"
  };
};
